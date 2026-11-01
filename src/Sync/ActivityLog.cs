using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Logging;

namespace EmbyCollections.Sync
{
    /// <summary>One line of sync activity, shown live on the settings page.</summary>
    public sealed class ActivityEntry
    {
        public long Seq { get; set; }

        public DateTime Time { get; set; }

        /// <summary>Info, Warn, Error or Done.</summary>
        public string Level { get; set; }

        public string CollectionId { get; set; }

        public string Collection { get; set; }

        public string Message { get; set; }
    }

    /// <summary>
    /// In-memory ring of recent sync activity (cleared on restart). Every line also goes to the Emby log.
    /// </summary>
    public static class ActivityLog
    {
        private const int Capacity = 2000;
        private static readonly object Sync = new object();
        private static readonly LinkedList<ActivityEntry> Entries = new LinkedList<ActivityEntry>();
        private static long seq;

        public static long LastSeq
        {
            get
            {
                lock (Sync)
                {
                    return seq;
                }
            }
        }

        public static void Add(string level, string collectionId, string collection, string message, ILogger logger = null)
        {
            lock (Sync)
            {
                Entries.AddLast(new ActivityEntry
                {
                    Seq = ++seq,
                    Time = DateTime.UtcNow,
                    Level = level,
                    CollectionId = collectionId,
                    Collection = collection,
                    Message = message,
                });
                while (Entries.Count > Capacity)
                {
                    Entries.RemoveFirst();
                }
            }

            var line = string.IsNullOrEmpty(collection) ? message : collection + ": " + message;
            if (logger == null)
            {
                return;
            }

            if (level == "Error")
            {
                logger.Error("{0}", line);
            }
            else if (level == "Warn")
            {
                logger.Warn("{0}", line);
            }
            else
            {
                logger.Info("{0}", line);
            }
        }

        /// <param name="after">Only entries newer than this sequence number.</param>
        /// <param name="collectionId">Only this collection's entries (plus run-wide ones) when set.</param>
        public static List<ActivityEntry> Since(long after, string collectionId, int max = 500)
        {
            lock (Sync)
            {
                return Entries
                    .Where(e => e.Seq > after && (string.IsNullOrEmpty(collectionId) || e.CollectionId == collectionId || e.CollectionId == null))
                    .Reverse()
                    .Take(max)
                    .Reverse()
                    .ToList();
            }
        }
    }

    /// <summary>Logs one collection's sync steps to the activity log and the Emby log.</summary>
    public sealed class SyncTrace
    {
        private readonly string id;
        private readonly string name;
        private readonly ILogger logger;

        public SyncTrace(string id, string name, ILogger logger)
        {
            this.id = id;
            this.name = name;
            this.logger = logger;
        }

        public void Info(string message) => ActivityLog.Add("Info", this.id, this.name, message, this.logger);

        public void Warn(string message) => ActivityLog.Add("Warn", this.id, this.name, message, this.logger);

        public void Error(string message) => ActivityLog.Add("Error", this.id, this.name, message, this.logger);

        public void Done(string message) => ActivityLog.Add("Done", this.id, this.name, message, this.logger);
    }
}
