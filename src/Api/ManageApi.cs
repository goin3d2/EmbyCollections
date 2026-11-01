using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbyCollections.Artwork;
using EmbyCollections.Model;
using EmbyCollections.Sync;
using EmbyCollections.Tasks;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace EmbyCollections.Api
{
    [Route("/EmbyCollections/Existing", "GET", Summary = "Every collection on the server")]
    [Authenticated(Roles = "Admin")]
    public class ListExisting : IReturnVoid
    {
    }

    [Route("/EmbyCollections/Existing/{Id}/Items", "GET", Summary = "Members of a collection")]
    [Authenticated(Roles = "Admin")]
    public class ListExistingItems : IReturnVoid
    {
        public long Id { get; set; }
    }

    [Route("/EmbyCollections/Existing/{Id}/Action", "POST", Summary = "Edit, delete, adopt, change artwork or remove members")]
    [Authenticated(Roles = "Admin")]
    public class ExistingAction : IRequiresRequestStream, IReturnVoid
    {
        public long Id { get; set; }

        public Stream RequestStream { get; set; }
    }

    public class ExistingActionRequest
    {
        /// <summary>Update | Delete | Adopt | SetArtwork | RemoveArtwork | RemoveItems</summary>
        public string Action { get; set; }

        public string Name { get; set; }

        public string SortName { get; set; }

        public string Overview { get; set; }

        /// <summary>For SetArtwork: Url, Kometa, TmdbCollection, Generated or Upload.</summary>
        public string ArtworkMode { get; set; }

        public ArtworkDefinition Artwork { get; set; }

        /// <summary>For Upload: the image, base64.</summary>
        public string Data { get; set; }

        public string MimeType { get; set; }

        public long[] ItemIds { get; set; }
    }

    /// <summary>Basic management of every collection on the server, managed by this plugin or not.</summary>
    public class ManageApi : IService, IRequiresRequest
    {
        private readonly IHttpResultFactory resultFactory;
        private readonly ILibraryManager libraryManager;
        private readonly ICollectionManager collectionManager;
        private readonly IProviderManager providerManager;
        private readonly IFileSystem fileSystem;
        private readonly ILogger logger;

        public ManageApi(
            IHttpResultFactory resultFactory,
            ILibraryManager libraryManager,
            ICollectionManager collectionManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            ILogManager logManager)
        {
            this.resultFactory = resultFactory;
            this.libraryManager = libraryManager;
            this.collectionManager = collectionManager;
            this.providerManager = providerManager;
            this.fileSystem = fileSystem;
            this.logger = logManager.GetLogger(Plugin.PluginName);
        }

        public IRequest Request { get; set; }

        private static Plugin Current => Plugin.Instance ?? throw new InvalidOperationException("Emby Collections is not loaded");

        public object Get(ListExisting request)
        {
            var managed = Current.Settings.Collections.Where(c => c.BoxSetId > 0).ToDictionary(c => c.BoxSetId, c => c);
            var boxes = this.libraryManager.GetItemList(new InternalItemsQuery { Recursive = true, IncludeItemTypes = new[] { "BoxSet" } })
                .OfType<BoxSet>()
                .OrderBy(b => b.SortName ?? b.Name, StringComparer.OrdinalIgnoreCase)
                .Select(b =>
                {
                    managed.TryGetValue(b.InternalId, out var def);
                    return new
                    {
                        Id = b.InternalId,
                        b.Name,
                        b.SortName,
                        b.Overview,
                        Count = this.Members(b.InternalId).Length,
                        HasPoster = b.HasImage(ImageType.Primary, 0),
                        ManagedId = def?.Id,
                        ManagedSources = def?.Sources.Count ?? 0,
                    };
                })
                .ToList();
            return this.Json(boxes);
        }

        public object Get(ListExistingItems request)
        {
            var ids = this.Members(request.Id);
            var items = ids.Select(id => this.libraryManager.GetItemById(id))
                .Where(i => i != null)
                .OrderBy(i => i.SortName ?? i.Name, StringComparer.OrdinalIgnoreCase)
                .Select(i => new { Id = i.InternalId, i.Name, Year = i.ProductionYear, Type = i.GetType().Name, HasPoster = i.HasImage(ImageType.Primary, 0) });
            return this.Json(items);
        }

        public async Task<object> Post(ExistingAction request)
        {
            using var reader = new StreamReader(request.RequestStream);
            var body = JsonSerializer.Deserialize<ExistingActionRequest>(await reader.ReadToEndAsync().ConfigureAwait(false), Plugin.Json)
                ?? throw new ArgumentException("Empty request");
            var box = this.libraryManager.GetItemById(request.Id) as BoxSet ?? throw new ArgumentException($"Collection {request.Id} not found");
            var def = Current.Settings.Collections.FirstOrDefault(c => c.BoxSetId == box.InternalId);

            switch ((body.Action ?? string.Empty).ToLowerInvariant())
            {
                case "update":
                    return this.Json(this.Update(box, def, body));
                case "delete":
                    this.libraryManager.DeleteItem(box, new DeleteOptions { DeleteFileLocation = true });
                    if (def != null)
                    {
                        Current.Update(s => s.Collections.RemoveAll(c => c.Id == def.Id));
                    }

                    this.logger.Info("Deleted collection {0}", box.Name);
                    return this.Json(new { Ok = true });
                case "adopt":
                    return this.Json(this.Adopt(box, def));
                case "setartwork":
                    return this.Json(await this.SetArtworkAsync(box, def, body).ConfigureAwait(false));
                case "removeartwork":
                    PosterWriter.Remove(box);
                    if (def != null)
                    {
                        Current.Update(s => this.ManualArtwork(s, def.Id));
                    }

                    return this.Json(new { Ok = true });
                case "removeitems":
                    var ids = (body.ItemIds ?? Array.Empty<long>()).Distinct().ToArray();
                    if (ids.Length > 0)
                    {
                        this.collectionManager.RemoveFromCollection(box, ids);
                    }

                    return this.Json(new { Ok = true, Removed = ids.Length, Note = def != null && def.Sources.Count > 0 && !def.OnlyAdd ? "This collection is synced from sources, so titles still in a source come back on the next sync." : null });
                default:
                    throw new ArgumentException($"Unknown action '{body.Action}'");
            }
        }

        private object Update(BoxSet box, CollectionDefinition def, ExistingActionRequest body)
        {
            var name = body.Name?.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                box.Name = name;
            }

            box.SortName = string.IsNullOrWhiteSpace(body.SortName) ? null : body.SortName.Trim();
            box.Overview = body.Overview ?? string.Empty;
            box.UpdateToRepository(ItemUpdateType.MetadataEdit);
            if (def != null)
            {
                // Keep the plugin's definition in step, or the next sync would undo the edit.
                Current.Update(s =>
                {
                    var d = s.Collections.FirstOrDefault(c => c.Id == def.Id);
                    if (d != null)
                    {
                        d.Name = box.Name;
                        d.SortName = body.SortName?.Trim() ?? string.Empty;
                        d.Overview = body.Overview ?? string.Empty;
                    }
                });
            }

            return new { Ok = true };
        }

        /// <summary>Puts an existing collection under the plugin, with no sources and add-only, so nothing in it changes.</summary>
        private object Adopt(BoxSet box, CollectionDefinition existing)
        {
            if (existing != null)
            {
                return new { Ok = true, existing.Id };
            }

            var def = new CollectionDefinition
            {
                Name = box.Name,
                SortName = box.SortName ?? string.Empty,
                Overview = box.Overview ?? string.Empty,
                BoxSetId = box.InternalId,
                OnlyAdd = true,
                Artwork = new ArtworkDefinition { Mode = ArtworkMode.None },
            };
            var tags = box.Tags ?? Array.Empty<string>();
            if (!tags.Contains(def.Tag, StringComparer.OrdinalIgnoreCase))
            {
                box.SetTags(tags.Append(def.Tag));
                box.UpdateToRepository(ItemUpdateType.MetadataEdit);
            }

            Current.Update(s => s.Collections.Add(def));
            this.logger.Info("Adopted collection {0}", box.Name);
            return new { Ok = true, def.Id };
        }

        private async Task<object> SetArtworkAsync(BoxSet box, CollectionDefinition def, ExistingActionRequest body)
        {
            ArtworkImage image;
            var warnings = new List<string>();
            if (string.Equals(body.ArtworkMode, "Upload", StringComparison.OrdinalIgnoreCase))
            {
                var data = Convert.FromBase64String(body.Data ?? string.Empty);
                if (data.Length == 0)
                {
                    throw new ArgumentException("No image uploaded");
                }

                image = new ArtworkImage { Data = data, MimeType = string.IsNullOrEmpty(body.MimeType) ? "image/jpeg" : body.MimeType, Source = "upload", Key = "upload|" + ArtworkHttp.Hash(Convert.ToBase64String(data, 0, Math.Min(data.Length, 4096)) + data.Length) };
            }
            else
            {
                if (!Enum.TryParse<ArtworkMode>(body.ArtworkMode, true, out var mode) || mode == ArtworkMode.None)
                {
                    throw new ArgumentException($"Unknown artwork mode '{body.ArtworkMode}'");
                }

                var art = body.Artwork ?? new ArtworkDefinition();
                art.Mode = mode;
                var temp = new CollectionDefinition { Name = box.Name, Artwork = art };
                var context = new ArtworkContext
                {
                    Collection = temp,
                    Settings = Current.Settings,
                    MemberPosters = art.Style == GeneratedStyle.Mosaic ? this.MemberPosters(this.Members(box.InternalId), 9) : Array.Empty<string>(),
                };
                image = await SyncRunner.BuildArtwork().ResolveAsync(context, warnings, CancellationToken.None).ConfigureAwait(false);
                if (image == null)
                {
                    return new { Ok = false, Message = warnings.Count > 0 ? string.Join("; ", warnings) : "No poster found for that choice" };
                }
            }

            await PosterWriter.SaveAsync(this.providerManager, this.libraryManager, this.fileSystem, box, image, CancellationToken.None).ConfigureAwait(false);
            if (def != null)
            {
                // A manual poster wins: stop sync from replacing it.
                Current.Update(s => this.ManualArtwork(s, def.Id));
            }

            this.logger.Info("{0}: poster set from {1}", box.Name, image.Source);
            return new { Ok = true, image.Source };
        }

        private void ManualArtwork(Settings s, string id)
        {
            var d = s.Collections.FirstOrDefault(c => c.Id == id);
            if (d != null)
            {
                d.Artwork.Mode = ArtworkMode.None;
            }
        }

        private long[] Members(long boxId)
        {
            return this.libraryManager.GetInternalItemIds(new InternalItemsQuery { CollectionIds = new[] { boxId }, Recursive = true });
        }

        private IReadOnlyList<string> MemberPosters(IEnumerable<long> members, int count)
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

        private object Json(object value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Plugin.Json);
            return this.resultFactory.GetResult(this.Request, new ReadOnlyMemory<byte>(bytes), "application/json", new Dictionary<string, string> { ["Cache-Control"] = "no-store" });
        }
    }
}
