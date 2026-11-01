using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbyCollections.Model;
using EmbyCollections.Sources;
using EmbyCollections.Sync;
using EmbyCollections.Tasks;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace EmbyCollections.Api
{
    [Route("/EmbyCollections/Settings", "GET", Summary = "Gets Emby Collections settings")]
    [Authenticated(Roles = "Admin")]
    public class GetSettings : IReturnVoid
    {
    }

    [Route("/EmbyCollections/Settings", "POST", Summary = "Saves Emby Collections settings")]
    [Authenticated(Roles = "Admin")]
    public class SaveSettings : IRequiresRequestStream, IReturnVoid
    {
        public Stream RequestStream { get; set; }
    }

    [Route("/EmbyCollections/Sync", "POST", Summary = "Starts a sync of one collection (Id) or all enabled collections")]
    [Authenticated(Roles = "Admin")]
    public class StartSync : IReturnVoid
    {
        public string Id { get; set; }
    }

    [Route("/EmbyCollections/Status", "GET", Summary = "Sync status of each collection")]
    [Authenticated(Roles = "Admin")]
    public class GetStatus : IReturnVoid
    {
    }

    [Route("/EmbyCollections/XtreamCategories", "GET", Summary = "Categories in xtreamfs-emby's categories.json")]
    [Authenticated(Roles = "Admin")]
    public class GetXtreamCategories : IReturnVoid
    {
    }

    [Route("/EmbyCollections/TestKey", "POST", Summary = "Tests an MDBList or TMDB key (the one typed, not necessarily saved)")]
    [Authenticated(Roles = "Admin")]
    public class TestKey : IRequiresRequestStream, IReturnVoid
    {
        public Stream RequestStream { get; set; }
    }

    public class TestKeyRequest
    {
        public string Service { get; set; }

        public string Key { get; set; }
    }

    [Route("/EmbyCollections/Activity", "GET", Summary = "Recent sync activity, newest last")]
    [Authenticated(Roles = "Admin")]
    public class GetActivity : IReturnVoid
    {
        public long After { get; set; }

        public string CollectionId { get; set; }
    }

    [Route("/EmbyCollections/KometaSearch", "GET", Summary = "Kometa default posters matching a name")]
    [Authenticated(Roles = "Admin")]
    public class SearchKometa : IReturnVoid
    {
        public string Query { get; set; }
    }

    public class CollectionsApi : IService, IRequiresRequest
    {
        private readonly IHttpResultFactory resultFactory;
        private readonly ILibraryManager libraryManager;
        private readonly ICollectionManager collectionManager;
        private readonly IProviderManager providerManager;
        private readonly IFileSystem fileSystem;
        private readonly ILogger logger;

        public CollectionsApi(
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

        public object Get(GetSettings request)
        {
            return this.Json(Current.Settings);
        }

        public async Task<object> Post(SaveSettings request)
        {
            using var reader = new StreamReader(request.RequestStream);
            var text = await reader.ReadToEndAsync().ConfigureAwait(false);
            var incoming = string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<Settings>(text, Plugin.Json);
            if (incoming == null)
            {
                throw new ArgumentException("No settings in the request");
            }

            Current.SaveFromPage(incoming);
            return this.Json(Current.Settings);
        }

        public object Post(StartSync request)
        {
            if (SyncRunner.IsRunning)
            {
                return this.Json(new { Started = false, Message = "A sync is already running" });
            }

            var id = string.IsNullOrWhiteSpace(request.Id) ? null : request.Id;
            var runner = new SyncRunner(this.libraryManager, this.collectionManager, this.providerManager, this.fileSystem, this.logger);
            _ = Task.Run(async () =>
            {
                try
                {
                    await runner.RunAsync(id, null, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logger.ErrorException("Sync failed", ex);
                }
            });
            return this.Json(new { Started = true });
        }

        public object Get(GetStatus request)
        {
            return this.Json(new
            {
                Running = SyncRunner.IsRunning,
                Collections = Current.Settings.Collections.Select(c => new { c.Id, c.Name, c.BoxSetId, c.LastSync }),
            });
        }

        public object Get(GetXtreamCategories request)
        {
            var source = new XtreamFsCategorySource(() => Current.XtreamFsDataPath);
            return this.Json(new
            {
                Path = source.IndexPath,
                Found = File.Exists(source.IndexPath),
                Categories = source.ListCategories(),
            });
        }

        public async Task<object> Post(TestKey request)
        {
            using var reader = new StreamReader(request.RequestStream);
            var body = JsonSerializer.Deserialize<TestKeyRequest>(await reader.ReadToEndAsync().ConfigureAwait(false), Plugin.Json) ?? new TestKeyRequest();
            (bool Ok, string Message) result;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                result = string.Equals(body.Service, "tmdb", StringComparison.OrdinalIgnoreCase)
                    ? await new Artwork.TmdbClient(body.Key).TestAsync(cts.Token).ConfigureAwait(false)
                    : await MdbListSource.TestKeyAsync(body.Key, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = (false, "Could not reach the service: " + ex.Message);
            }

            return this.Json(new { result.Ok, result.Message });
        }

        public object Get(GetActivity request)
        {
            return this.Json(new
            {
                Running = SyncRunner.IsRunning,
                Last = ActivityLog.LastSeq,
                Entries = ActivityLog.Since(request.After, request.CollectionId),
            });
        }

        public async Task<object> Get(SearchKometa request)
        {
            var paths = await SyncRunner.Kometa.SearchAsync(request.Query, 24, CancellationToken.None).ConfigureAwait(false);
            return this.Json(paths.Select(p => new
            {
                Path = p,
                Url = Artwork.KometaArtworkProvider.RawBase + string.Join("/", p.Split('/').Select(Uri.EscapeDataString)),
            }));
        }

        private object Json(object value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Plugin.Json);
            return this.resultFactory.GetResult(this.Request, new ReadOnlyMemory<byte>(bytes), "application/json", new Dictionary<string, string> { ["Cache-Control"] = "no-store" });
        }
    }
}
