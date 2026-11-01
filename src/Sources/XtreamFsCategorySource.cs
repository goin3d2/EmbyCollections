using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbyCollections.Model;

namespace EmbyCollections.Sources
{
    /// <summary>
    /// Provider categories from xtreamfs-emby's categories.json (written after each xtreamfs refresh).
    /// Value is "movie:&lt;category id&gt;" or "series:&lt;category id&gt;". Items are matched by path first
    /// (exact, covers titles without a TMDB id) and TMDB id second.
    /// </summary>
    public sealed class XtreamFsCategorySource : ICollectionSource
    {
        private readonly Func<string> dataPath;

        public XtreamFsCategorySource(Func<string> dataPath)
        {
            this.dataPath = dataPath;
        }

        public SourceType Type => SourceType.XtreamCategory;

        public string IndexPath => Path.Combine(this.dataPath(), "categories.json");

        public sealed class CategoryInfo
        {
            public string Value { get; set; }

            public string Kind { get; set; }

            public string Id { get; set; }

            public string Name { get; set; }

            public int Count { get; set; }
        }

        /// <summary>Every category in the index, for the settings page.</summary>
        public List<CategoryInfo> ListCategories()
        {
            var list = new List<CategoryInfo>();
            if (!File.Exists(this.IndexPath))
            {
                return list;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(this.IndexPath));
            foreach (var (section, kind) in new[] { ("movies", "movie"), ("series", "series") })
            {
                if (!doc.RootElement.TryGetProperty(section, out var cats) || cats.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var cat in cats.EnumerateObject())
                {
                    var name = cat.Value.TryGetProperty("name", out var n) ? n.GetString() : cat.Name;
                    var count = cat.Value.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 0;
                    list.Add(new CategoryInfo { Value = kind + ":" + cat.Name, Kind = kind, Id = cat.Name, Name = name, Count = count });
                }
            }

            return list.OrderBy(c => c.Kind).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public Task<SourceResult> ResolveAsync(SourceDefinition source, CancellationToken cancellationToken)
        {
            var result = new SourceResult();
            if (!File.Exists(this.IndexPath))
            {
                throw new InvalidOperationException(
                    $"No xtreamfs category index at {this.IndexPath}. Update xtreamfs-emby and run a refresh, or set the xtreamfs data folder.");
            }

            var parts = (source.Value ?? string.Empty).Split(new[] { ':' }, 2);
            if (parts.Length != 2)
            {
                throw new ArgumentException($"xtreamfs category should look like movie:123 or series:45, not '{source.Value}'");
            }

            var isMovie = parts[0].StartsWith("movie", StringComparison.OrdinalIgnoreCase);
            var section = isMovie ? "movies" : "series";
            using var doc = JsonDocument.Parse(File.ReadAllText(this.IndexPath));
            if (!doc.RootElement.TryGetProperty(section, out var cats) || !cats.TryGetProperty(parts[1], out var cat))
            {
                result.Warnings.Add($"xtreamfs category {source.Value} is not in the index (not selected in xtreamfs, or removed by the provider)");
                return Task.FromResult(result);
            }

            if (cat.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    result.Refs.Add(new MediaRef
                    {
                        Kind = isMovie ? MediaKind.Movie : MediaKind.Show,
                        Path = item.TryGetProperty("path", out var p) ? p.GetString() : null,
                        Tmdb = item.TryGetProperty("tmdb", out var t) ? t.GetString() : null,
                    });
                }
            }

            var name = cat.TryGetProperty("name", out var n) ? n.GetString() : parts[1];
            result.Notes.Add($"xtreamfs {(isMovie ? "movie" : "series")} category '{name}': {result.Refs.Count} titles " +
                             $"({result.Refs.Count(r => r.Tmdb != null)} with a TMDB id) from {this.IndexPath}");
            return Task.FromResult(result);
        }
    }
}
