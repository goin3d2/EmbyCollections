using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyCollections.Artwork;
using EmbyCollections.Matching;
using EmbyCollections.Model;
using EmbyCollections.Sources;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

namespace EmbyCollections.Sync
{
    /// <summary>
    /// Brings one managed collection in line with its sources: resolve sources, match the library,
    /// create the BoxSet if needed, add/remove members, then apply artwork when it changed.
    /// Only BoxSets this plugin created (tagged embycollections:&lt;id&gt;) are ever touched.
    /// </summary>
    public sealed class CollectionSyncer
    {
        private readonly ILibraryManager libraryManager;
        private readonly ICollectionManager collectionManager;
        private readonly IProviderManager providerManager;
        private readonly IFileSystem fileSystem;
        private readonly ILogger logger;
        private readonly Dictionary<SourceType, ICollectionSource> sources;
        private readonly ArtworkResolver artwork;
        private readonly LibraryMatcher matcher;

        public CollectionSyncer(
            ILibraryManager libraryManager,
            ICollectionManager collectionManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            ILogger logger,
            IEnumerable<ICollectionSource> sources,
            ArtworkResolver artwork)
        {
            this.libraryManager = libraryManager;
            this.collectionManager = collectionManager;
            this.providerManager = providerManager;
            this.fileSystem = fileSystem;
            this.logger = logger;
            this.sources = sources.ToDictionary(s => s.Type);
            this.artwork = artwork;
            this.matcher = new LibraryMatcher(libraryManager);
        }

        public async Task<CollectionStatus> SyncAsync(CollectionDefinition def, Settings settings, CancellationToken ct)
        {
            var status = new CollectionStatus { Time = DateTime.UtcNow };
            var refs = new List<MediaRef>();
            var direct = new List<long>();
            var failedSources = 0;

            foreach (var src in def.Sources)
            {
                ct.ThrowIfCancellationRequested();
                if (!this.sources.TryGetValue(src.Type, out var source))
                {
                    status.Warnings.Add($"Unknown source type {src.Type}");
                    failedSources++;
                    continue;
                }

                try
                {
                    var r = await source.ResolveAsync(src, ct).ConfigureAwait(false);
                    refs.AddRange(r.Refs);
                    direct.AddRange(r.ItemIds);
                    status.Warnings.AddRange(r.Warnings);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedSources++;
                    status.Warnings.Add($"{src.Type} '{(string.IsNullOrEmpty(src.Label) ? src.Value : src.Label)}': {ex.Message}");
                    this.logger.Warn("{0}: source {1} {2} failed: {3}", def.Name, src.Type, src.Value, ex.Message);
                }
            }

            status.SourceItems = refs.Count + direct.Count;
            var match = this.matcher.Match(refs, ct);
            var desired = new List<long>(match.ItemIds);
            var seen = new HashSet<long>(desired);
            desired.AddRange(direct.Where(seen.Add));
            status.Matched = match.Matched + direct.Count;

            var box = this.FindBoxSet(def);
            if (box == null)
            {
                if (desired.Count == 0)
                {
                    status.Warnings.Add("Nothing in the library matched yet, so the collection wasn't created");
                    return status;
                }

                box = await this.collectionManager.CreateCollection(new CollectionCreationOptions
                {
                    Name = def.Name,
                    IsLocked = true,
                    ItemIdList = desired.ToArray(),
                }).ConfigureAwait(false);
                status.Added = desired.Count;
                this.logger.Info("{0}: created collection with {1} items", def.Name, desired.Count);
            }
            else
            {
                var current = this.libraryManager.GetInternalItemIds(
                    new InternalItemsQuery { CollectionIds = new[] { box.InternalId }, Recursive = true },
                    ct);
                var currentSet = new HashSet<long>(current);
                var desiredSet = new HashSet<long>(desired);
                var add = desired.Where(id => !currentSet.Contains(id)).ToArray();

                // A failed source would look like "everything was removed"; never remove on a partial read.
                var remove = settings.RemoveMissing && !def.OnlyAdd && def.Sources.Count > 0 && failedSources == 0
                    ? current.Where(id => !desiredSet.Contains(id)).ToArray()
                    : Array.Empty<long>();
                if (failedSources > 0 && current.Any(id => !desiredSet.Contains(id)))
                {
                    status.Warnings.Add("A source failed, so no items were removed this run");
                }

                if (add.Length > 0)
                {
                    await this.collectionManager.AddToCollection(box.InternalId, add).ConfigureAwait(false);
                }

                if (remove.Length > 0)
                {
                    this.collectionManager.RemoveFromCollection(box, remove);
                }

                status.Added = add.Length;
                status.Removed = remove.Length;
                if (add.Length + remove.Length > 0)
                {
                    this.logger.Info("{0}: +{1} -{2} (now {3})", def.Name, add.Length, remove.Length, currentSet.Count + add.Length - remove.Length);
                }
            }

            def.BoxSetId = box.InternalId;
            this.ApplyMetadata(box, def);
            await this.ApplyArtworkAsync(box, def, settings, desired, status, ct).ConfigureAwait(false);
            return status;
        }

        private BoxSet FindBoxSet(CollectionDefinition def)
        {
            if (def.BoxSetId > 0 && this.libraryManager.GetItemById(def.BoxSetId) is BoxSet byId)
            {
                return byId;
            }

            return this.libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = new[] { "BoxSet" },
                Tags = new[] { def.Tag },
            }).OfType<BoxSet>().FirstOrDefault();
        }

        private void ApplyMetadata(BoxSet box, CollectionDefinition def)
        {
            var changed = false;
            var tags = box.Tags ?? Array.Empty<string>();
            if (!tags.Contains(def.Tag, StringComparer.OrdinalIgnoreCase))
            {
                box.SetTags(tags.Append(def.Tag));
                changed = true;
            }

            if (!string.Equals(box.Name, def.Name, StringComparison.Ordinal))
            {
                box.Name = def.Name;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(def.SortName) && box.SortName != def.SortName)
            {
                box.SortName = def.SortName;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(def.Overview) && box.Overview != def.Overview)
            {
                box.Overview = def.Overview;
                changed = true;
            }

            if (changed)
            {
                box.UpdateToRepository(ItemUpdateType.MetadataEdit);
            }
        }

        private async Task ApplyArtworkAsync(BoxSet box, CollectionDefinition def, Settings settings, List<long> members, CollectionStatus status, CancellationToken ct)
        {
            var context = new ArtworkContext
            {
                Collection = def,
                Settings = settings,
                MemberPosters = def.Artwork.Style == GeneratedStyle.Mosaic ? this.MemberPosters(members, 9) : Array.Empty<string>(),
            };
            var image = await this.artwork.ResolveAsync(context, status.Warnings, ct).ConfigureAwait(false);
            if (image == null)
            {
                return;
            }

            status.ArtworkSource = image.Source;
            if (image.Key == def.Artwork.AppliedKey && box.HasImage(ImageType.Primary, 0))
            {
                return;
            }

            await PosterWriter.SaveAsync(this.providerManager, this.libraryManager, this.fileSystem, box, image, ct).ConfigureAwait(false);
            def.Artwork.AppliedKey = image.Key;
            this.logger.Info("{0}: poster set from {1}", def.Name, image.Source);
        }

        public IReadOnlyList<string> MemberPosters(IEnumerable<long> members, int count)
        {
            var list = new List<string>();
            foreach (var id in members)
            {
                if (list.Count >= count)
                {
                    break;
                }

                var path = this.libraryManager.GetItemById(id)?.GetImagePath(ImageType.Primary, 0);
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    list.Add(path);
                }
            }

            return list;
        }
    }

    /// <summary>Saves a poster onto an item. Shared by sync and the "All collections" editor.</summary>
    public static class PosterWriter
    {
        public static async Task SaveAsync(IProviderManager providerManager, ILibraryManager libraryManager, IFileSystem fileSystem, BaseItem item, ArtworkImage image, CancellationToken ct)
        {
            using var stream = new MemoryStream(image.Data);
            await providerManager.SaveImage(
                item,
                libraryManager.GetLibraryOptions(item),
                stream,
                image.MimeType.AsMemory(),
                ImageType.Primary,
                null,
                Array.Empty<long>(),
                new DirectoryService(fileSystem),
                true,
                ct).ConfigureAwait(false);

            // SaveImage writes the file and sets the image on the item; the item still has to be saved.
            item.UpdateToRepository(ItemUpdateType.ImageUpdate);
        }

        public static void Remove(BaseItem item)
        {
            if (item.HasImage(ImageType.Primary, 0))
            {
                item.DeleteImage(ImageType.Primary, 0);
                item.UpdateToRepository(ItemUpdateType.ImageUpdate);
            }
        }
    }
}
