using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyCollections.Artwork;
using EmbyCollections.Model;
using EmbyCollections.Sources;
using EmbyCollections.Sync;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace EmbyCollections.Tasks
{
    /// <summary>Wires sources, artwork and the syncer together. Shared by the scheduled task and "Sync now".</summary>
    public sealed class SyncRunner
    {
        private static readonly SemaphoreSlim Running = new SemaphoreSlim(1, 1);

        private readonly CollectionSyncer syncer;
        private readonly ILogger logger;

        public SyncRunner(ILibraryManager libraryManager, ICollectionManager collectionManager, IProviderManager providerManager, IFileSystem fileSystem, ILogger logger)
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("Emby Collections is not loaded");
            this.logger = logger;
            var sources = new ICollectionSource[]
            {
                new MdbListSource(() => plugin.Settings.MdbListApiKey),
                new XtreamFsCategorySource(() => plugin.XtreamFsDataPath),
                new LibraryTagSource(libraryManager),
            };
            this.syncer = new CollectionSyncer(libraryManager, collectionManager, providerManager, fileSystem, logger, sources, BuildArtwork());
        }

        public static ArtworkResolver BuildArtwork()
        {
            return new ArtworkResolver(new IArtworkProvider[]
            {
                new UrlArtworkProvider(),
                Kometa,
                new TmdbCollectionArtworkProvider(),
                new GeneratedArtworkProvider(),
            });
        }

        /// <summary>One shared instance so the Kometa file index is fetched once per server run.</summary>
        public static KometaArtworkProvider Kometa { get; } = new KometaArtworkProvider(() => Plugin.Instance.DataDirectory);

        public static bool IsRunning => Running.CurrentCount == 0;

        /// <param name="onlyId">Sync just this collection (enabled or not); null syncs every enabled one.</param>
        public async Task RunAsync(string onlyId, IProgress<double> progress, CancellationToken ct)
        {
            if (!await Running.WaitAsync(0, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException("A collection sync is already running");
            }

            try
            {
                var plugin = Plugin.Instance;
                var settings = plugin.Settings;
                var todo = settings.Collections
                    .Where(c => onlyId == null ? c.Enabled : c.Id == onlyId)
                    .Select(c => c.Id)
                    .ToList();
                for (var i = 0; i < todo.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var def = plugin.Settings.Collections.FirstOrDefault(c => c.Id == todo[i]);
                    if (def == null)
                    {
                        continue;
                    }

                    CollectionStatus status;
                    try
                    {
                        status = await this.syncer.SyncAsync(def, plugin.Settings, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        this.logger.ErrorException("{0}: sync failed", ex, def.Name);
                        status = new CollectionStatus { Time = DateTime.UtcNow, Error = ex.Message };
                    }

                    // The syncer updated BoxSetId/AppliedKey on def; save those with the status.
                    var boxId = def.BoxSetId;
                    var applied = def.Artwork.AppliedKey;
                    plugin.Update(s =>
                    {
                        var target = s.Collections.FirstOrDefault(c => c.Id == def.Id);
                        if (target != null)
                        {
                            target.BoxSetId = boxId;
                            target.Artwork.AppliedKey = applied;
                            target.LastSync = status;
                        }
                    });
                    progress?.Report(100.0 * (i + 1) / Math.Max(1, todo.Count));
                }
            }
            finally
            {
                Running.Release();
            }
        }
    }

    public class SyncCollectionsTask : IScheduledTask
    {
        private readonly ILibraryManager libraryManager;
        private readonly ICollectionManager collectionManager;
        private readonly IProviderManager providerManager;
        private readonly IFileSystem fileSystem;
        private readonly ILogger logger;

        public SyncCollectionsTask(ILibraryManager libraryManager, ICollectionManager collectionManager, IProviderManager providerManager, IFileSystem fileSystem, ILogManager logManager)
        {
            this.libraryManager = libraryManager;
            this.collectionManager = collectionManager;
            this.providerManager = providerManager;
            this.fileSystem = fileSystem;
            this.logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Name => "Sync collections";

        public string Key => "EmbyCollectionsSync";

        public string Description => "Updates every enabled Emby Collections collection from its sources and refreshes its artwork.";

        public string Category => Plugin.PluginName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerInterval, IntervalTicks = TimeSpan.FromHours(12).Ticks },
            };
        }

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            return new SyncRunner(this.libraryManager, this.collectionManager, this.providerManager, this.fileSystem, this.logger)
                .RunAsync(null, progress, cancellationToken);
        }
    }
}
