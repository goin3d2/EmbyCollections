using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using EmbyCollections.Sources;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace EmbyCollections.Matching
{
    public sealed class MatchResult
    {
        /// <summary>Matched library items, in source rank order.</summary>
        public List<long> ItemIds { get; } = new List<long>();

        public int Requested { get; set; }

        public int Matched { get; set; }

        public int ByPath { get; set; }

        public int ById { get; set; }

        /// <summary>Source titles not found in the library.</summary>
        public List<MediaRef> Unmatched { get; } = new List<MediaRef>();
    }

    /// <summary>
    /// Finds library movies/series for source refs: exact path first, then IMDb, TMDB and TVDB ids.
    /// TMDB ids are only compared within the same kind (movie vs show), since TMDB reuses numbers across them.
    /// </summary>
    public sealed class LibraryMatcher
    {
        private const int Batch = 400;

        private readonly ILibraryManager libraryManager;

        public LibraryMatcher(ILibraryManager libraryManager)
        {
            this.libraryManager = libraryManager;
        }

        public MatchResult Match(IReadOnlyList<MediaRef> refs, CancellationToken ct)
        {
            var result = new MatchResult { Requested = refs.Count };
            var found = new long[refs.Count];
            var byPathHit = new bool[refs.Count];

            // 1. Paths (xtreamfs): exact, case-sensitive like the file system.
            var byPath = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (var i = 0; i < refs.Count; i++)
            {
                if (!string.IsNullOrEmpty(refs[i].Path))
                {
                    Add(byPath, refs[i].Path, i);
                }
            }

            foreach (var chunk in byPath.Keys.Chunk(Batch))
            {
                ct.ThrowIfCancellationRequested();
                var items = this.libraryManager.GetItemList(
                    new InternalItemsQuery { Recursive = true, IncludeItemTypes = new[] { "Movie", "Series" }, PathIsAny = chunk },
                    ct);
                foreach (var item in items)
                {
                    if (item.Path != null && byPath.TryGetValue(item.Path, out var idx))
                    {
                        Mark(found, idx, item);
                        foreach (var i in idx)
                        {
                            byPathHit[i] = found[i] != 0;
                        }
                    }
                }
            }

            // 2. Provider ids, per kind.
            foreach (var (kind, types) in new[] { (MediaKind.Movie, new[] { "Movie" }), (MediaKind.Show, new[] { "Series" }) })
            {
                var keys = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < refs.Count; i++)
                {
                    var r = refs[i];
                    if (found[i] != 0 || (r.Kind != kind && r.Kind != MediaKind.Any))
                    {
                        continue;
                    }

                    if (r.Imdb != null)
                    {
                        Add(keys, "Imdb\u0001" + r.Imdb, i);
                    }

                    if (r.Tmdb != null)
                    {
                        Add(keys, "Tmdb\u0001" + r.Tmdb, i);
                    }

                    if (r.Tvdb != null)
                    {
                        Add(keys, "Tvdb\u0001" + r.Tvdb, i);
                    }
                }

                foreach (var chunk in keys.Keys.Chunk(Batch))
                {
                    ct.ThrowIfCancellationRequested();
                    var pairs = chunk.Select(k =>
                    {
                        var p = k.Split('\u0001');
                        return new KeyValuePair<string, string>(p[0], p[1]);
                    }).ToList();
                    var items = this.libraryManager.GetItemList(
                        new InternalItemsQuery { Recursive = true, IncludeItemTypes = types, AnyProviderIdEquals = pairs },
                        ct);
                    foreach (var item in items)
                    {
                        if (item.ProviderIds == null)
                        {
                            continue;
                        }

                        foreach (var pid in item.ProviderIds)
                        {
                            if (keys.TryGetValue(pid.Key + "\u0001" + pid.Value, out var idx))
                            {
                                Mark(found, idx, item);
                            }
                        }
                    }
                }
            }

            var seen = new HashSet<long>();
            for (var i = 0; i < found.Length; i++)
            {
                if (found[i] == 0)
                {
                    result.Unmatched.Add(refs[i]);
                    continue;
                }

                if (byPathHit[i])
                {
                    result.ByPath++;
                }
                else
                {
                    result.ById++;
                }

                result.Matched++;
                if (seen.Add(found[i]))
                {
                    result.ItemIds.Add(found[i]);
                }
            }

            return result;
        }

        private static void Add(Dictionary<string, List<int>> d, string key, int i)
        {
            if (!d.TryGetValue(key, out var list))
            {
                d[key] = list = new List<int>();
            }

            list.Add(i);
        }

        private static void Mark(long[] found, List<int> idx, BaseItem item)
        {
            if (item.IsVirtualItem)
            {
                return;
            }

            foreach (var i in idx)
            {
                if (found[i] == 0)
                {
                    found[i] = item.InternalId;
                }
            }
        }
    }
}
