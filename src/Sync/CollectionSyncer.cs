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
            var trace = new SyncTrace(def.Id, def.Name, this.logger);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var refs = new List<MediaRef>();
            var direct = new List<long>();
            var failedSources = 0;

            trace.Info(def.Sources.Count == 0
                ? "Sync started (no sources: titles are left as they are)"
                : $"Sync started: {def.Sources.Count} source{(def.Sources.Count == 1 ? string.Empty : "s")}" + (def.OnlyAdd ? ", add only" : string.Empty));
            foreach (var src in def.Sources)
            {
                ct.ThrowIfCancellationRequested();
                var label = string.IsNullOrEmpty(src.Label) ? src.Value : src.Label;
                if (!this.sources.TryGetValue(src.Type, out var source))
                {
                    status.Warnings.Add($"Unknown source type {src.Type}");
                    trace.Warn($"Unknown source type {src.Type}");
                    failedSources++;
                    continue;
                }

                try
                {
                    trace.Info($"Reading {src.Type} '{label}'…");
                    var t0 = clock.Elapsed;
                    var r = await source.ResolveAsync(src, ct).ConfigureAwait(false);
                    refs.AddRange(r.Refs);
                    direct.AddRange(r.ItemIds);
                    status.Warnings.AddRange(r.Warnings);
                    foreach (var note in r.Notes)
                    {
                        trace.Info($"{note} ({(clock.Elapsed - t0).TotalSeconds:0.0}s)");
                    }

                    foreach (var w in r.Warnings)
                    {
                        trace.Warn(w);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedSources++;
                    status.Warnings.Add($"{src.Type} '{label}': {ex.Message}");
                    trace.Error($"{src.Type} '{label}' failed: {ex.Message}");
                }
            }

            status.SourceItems = refs.Count + direct.Count;
            var match = this.matcher.Match(refs, ct);
            var desired = new List<long>(match.ItemIds);
            var seen = new HashSet<long>(desired);
            desired.AddRange(direct.Where(seen.Add));
            status.Matched = match.Matched + direct.Count;
            if (refs.Count > 0)
            {
                trace.Info($"Matched {match.Matched} of {refs.Count} source titles in the library ({match.ByPath} by file path, {match.ById} by IMDb/TMDB/TVDB id); {match.Unmatched.Count} not in the library");
                if (match.Unmatched.Count > 0)
                {
                    var sample = match.Unmatched.Take(8).Select(Describe);
                    trace.Info("Not in library: " + string.Join("; ", sample) + (match.Unmatched.Count > 8 ? $"; and {match.Unmatched.Count - 8} more" : string.Empty));
                }
            }

            if (direct.Count > 0)
            {
                trace.Info($"{direct.Count} titles came straight from the library (tags)");
            }

            var box = this.FindBoxSet(def);
            if (box == null)
            {
                if (desired.Count == 0)
                {
                    status.Warnings.Add("Nothing in the library matched yet, so the collection wasn't created");
                    trace.Warn("Nothing in the library matched, so the collection wasn't created");
                    trace.Done($"Finished in {clock.Elapsed.TotalSeconds:0.0}s");
                    return status;
                }

                box = await this.collectionManager.CreateCollection(new CollectionCreationOptions
                {
                    Name = def.Name,
                    IsLocked = true,
                    ItemIdList = desired.ToArray(),
                }).ConfigureAwait(false);
                status.Added = desired.Count;
                trace.Info($"Created the Emby collection with {desired.Count} titles: {this.Names(desired)}");
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
                    trace.Warn("A source failed, so nothing is removed this run");
                }
                else if ((def.OnlyAdd || !settings.RemoveMissing) && current.Any(id => !desiredSet.Contains(id)))
                {
                    trace.Info($"{current.Count(id => !desiredSet.Contains(id))} titles are no longer in any source but kept ({(def.OnlyAdd ? "add only" : "removing is turned off")})");
                }

                trace.Info($"Collection has {current.Length} titles; adding {add.Length}, removing {remove.Length}");

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
                if (add.Length > 0)
                {
                    trace.Info($"Added: {this.Names(add)}");
                }

                if (remove.Length > 0)
                {
                    trace.Info($"Removed: {this.Names(remove)}");
                }
            }

            def.BoxSetId = box.InternalId;
            var changes = this.ApplyMetadata(box, def);
            if (changes.Count > 0)
            {
                trace.Info("Updated " + string.Join(", ", changes));
            }

            await this.ApplyArtworkAsync(box, def, settings, desired, status, trace, ct).ConfigureAwait(false);
            trace.Done($"Finished in {clock.Elapsed.TotalSeconds:0.0}s: {status.Matched} matched, +{status.Added} −{status.Removed}" +
                       (status.Warnings.Count > 0 ? $", {status.Warnings.Count} warning{(status.Warnings.Count == 1 ? string.Empty : "s")}" : string.Empty));
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

        private static string Describe(MediaRef r)
        {
            var id = r.Imdb ?? (r.Tmdb != null ? "tmdb " + r.Tmdb : r.Tvdb != null ? "tvdb " + r.Tvdb : null);
            if (!string.IsNullOrEmpty(r.Title))
            {
                return id != null ? $"{r.Title} ({id})" : r.Title;
            }

            if (!string.IsNullOrEmpty(r.Path))
            {
                var name = Path.GetFileNameWithoutExtension(r.Path.TrimEnd('/', '\\'));
                return id != null ? $"{name} ({id})" : name;
            }

            return id ?? "?";
        }

        /// <summary>Up to 10 item names for the log.</summary>
        private string Names(IReadOnlyCollection<long> ids)
        {
            var names = ids.Take(10).Select(id => this.libraryManager.GetItemById(id)).Where(i => i != null)
                .Select(i => i.ProductionYear.HasValue ? $"{i.Name} ({i.ProductionYear})" : i.Name);
            return string.Join("; ", names) + (ids.Count > 10 ? $"; and {ids.Count - 10} more" : string.Empty);
        }

        private List<string> ApplyMetadata(BoxSet box, CollectionDefinition def)
        {
            var changes = new List<string>();
            var changed = false;
            var tags = box.Tags ?? Array.Empty<string>();
            if (!tags.Contains(def.Tag, StringComparer.OrdinalIgnoreCase))
            {
                box.SetTags(tags.Append(def.Tag));
                changes.Add("plugin tag");
                changed = true;
            }

            if (!string.Equals(box.Name, def.Name, StringComparison.Ordinal))
            {
                box.Name = def.Name;
                changes.Add("name");
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(def.SortName) && box.SortName != def.SortName)
            {
                box.SortName = def.SortName;
                changes.Add("sort name");
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(def.Overview) && box.Overview != def.Overview)
            {
                box.Overview = def.Overview;
                changes.Add("overview");
                changed = true;
            }

            var order = DisplayOrderFor(def.Sort);
            if (order.HasValue && box.DisplayOrder != order.Value)
            {
                box.DisplayOrder = order.Value;
                changes.Add("display order (" + (def.Sort == CollectionSort.Name ? "name" : "release date") + ")");
                changed = true;
            }

            if (changed)
            {
                box.UpdateToRepository(ItemUpdateType.MetadataEdit);
            }

            return changes;
        }

        public static CollectionDisplayOrder? DisplayOrderFor(CollectionSort sort)
        {
            return sort switch
            {
                CollectionSort.ReleaseDate => CollectionDisplayOrder.PremiereDate,
                CollectionSort.Name => CollectionDisplayOrder.SortName,
                _ => null,
            };
        }

        private async Task ApplyArtworkAsync(BoxSet box, CollectionDefinition def, Settings settings, List<long> members, CollectionStatus status, SyncTrace trace, CancellationToken ct)
        {
            var context = new ArtworkContext
            {
                Collection = def,
                Settings = settings,
                MemberPosters = def.Artwork.Style == GeneratedStyle.Mosaic ? this.MemberPosters(members, 9) : Array.Empty<string>(),
            };
            if (def.Artwork.Mode != ArtworkMode.None)
            {
                trace.Info($"Artwork mode {def.Artwork.Mode}: looking for a poster…");
            }

            var image = await this.artwork.ResolveAsync(context, status.Warnings, ct, trace.Info).ConfigureAwait(false);
            if (image == null)
            {
                if (def.Artwork.Mode != ArtworkMode.None)
                {
                    trace.Warn("No poster found; the collection keeps its current one");
                }

                return;
            }

            status.ArtworkSource = image.Source;
            if (image.Key == def.Artwork.AppliedKey && box.HasImage(ImageType.Primary, 0))
            {
                trace.Info("Poster unchanged since the last sync, not saved again");
                return;
            }

            await PosterWriter.SaveAsync(this.providerManager, this.libraryManager, this.fileSystem, box, image, ct).ConfigureAwait(false);
            def.Artwork.AppliedKey = image.Key;
            trace.Info($"Poster saved from {image.Source}");
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
