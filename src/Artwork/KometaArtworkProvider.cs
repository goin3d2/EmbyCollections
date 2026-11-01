using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbyCollections.Model;
using EmbyCollections.Sources;

namespace EmbyCollections.Artwork
{
    /// <summary>
    /// Posters from Kometa's Default-Images repo (streaming services, networks, studios, genres, franchises…).
    /// The file list is fetched once from GitHub and cached for a week; images are downloaded on use, never bundled.
    /// </summary>
    public sealed class KometaArtworkProvider : IArtworkProvider
    {
        public const string RawBase = "https://raw.githubusercontent.com/Kometa-Team/Default-Images/master/";

        /// <summary>Folders searched by name, best first. Only full posters (no overlays, logos or grids).</summary>
        private static readonly string[] Preferred =
        {
            "streaming/color/", "network/color/", "studio/", "franchise/", "universe/", "genre/", "seasonal/",
            "decade/", "year/", "country/color/", "chart/color/", "content_rating/", "award/",
        };

        private readonly Func<string> dataDir;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private List<string> index;
        private DateTime retryAfter = DateTime.MaxValue;

        public KometaArtworkProvider(Func<string> dataDir)
        {
            this.dataDir = dataDir;
        }

        public ArtworkMode Mode => ArtworkMode.Kometa;

        public async Task<ArtworkImage> GetAsync(ArtworkContext context, CancellationToken ct)
        {
            var art = context.Collection.Artwork;
            var path = art.KometaPath?.Trim().TrimStart('/');
            if (string.IsNullOrEmpty(path))
            {
                path = (await this.SearchAsync(context.Collection.Name, 1, ct).ConfigureAwait(false)).FirstOrDefault();
            }

            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var url = RawBase + string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
            var image = await ArtworkHttp.DownloadAsync(url, "kometa", ct).ConfigureAwait(false);
            image.Source = "kometa:" + path;
            return image;
        }

        /// <summary>Poster paths whose file name matches the query: exact name first, then names containing it.</summary>
        public async Task<List<string>> SearchAsync(string query, int limit, CancellationToken ct)
        {
            query = (query ?? string.Empty).Trim();
            if (query.Contains('/'))
            {
                query = Path.GetFileNameWithoutExtension(query); // a pasted path searches by its file name
            }

            var q = Norm(query);
            // "Disney Plus" / "Paramount Plus" are named with "+" in the image set.
            var alt = Norm(System.Text.RegularExpressions.Regex.Replace(query, @"\s*\bplus\b", "+", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            if (q.Length == 0)
            {
                return new List<string>();
            }

            var all = await this.GetIndexAsync(ct).ConfigureAwait(false);
            int Rank(string p)
            {
                for (var i = 0; i < Preferred.Length; i++)
                {
                    if (p.StartsWith(Preferred[i], StringComparison.Ordinal))
                    {
                        return i;
                    }
                }

                return Preferred.Length;
            }

            var scored = all
                .Select(p => (p, stem: Norm(Path.GetFileNameWithoutExtension(p))))
                .Select(x => (x.p, score: Math.Max(Score(x.stem, q), Score(x.stem, alt))))
                .Where(x => x.score >= 0)
                .OrderByDescending(x => x.score)
                .ThenBy(x => Rank(x.p))
                .ThenBy(x => x.p.Length)
                .Select(x => x.p);
            return scored.Take(Math.Max(1, limit)).ToList();
        }

        /// <summary>Higher is better: exact 3, starts with 2, contains 1, no match -1 (negated for ordering).</summary>
        private static int Score(string stem, string q)
        {
            if (q.Length == 0)
            {
                return -1;
            }

            return stem == q ? 3 : stem.StartsWith(q, StringComparison.Ordinal) ? 2 : stem.Contains(q, StringComparison.Ordinal) ? 1 : -1;
        }

        /// <summary>"Apple TV+ Movies" and "Apple TV+_movies" both become "appletv+movies".</summary>
        public static string Norm(string s)
        {
            var sb = new StringBuilder();
            foreach (var ch in (s ?? string.Empty).ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch) || ch == '+' || ch == '&')
                {
                    sb.Append(ch);
                }
            }

            return sb.ToString();
        }

        private async Task<List<string>> GetIndexAsync(CancellationToken ct)
        {
            if (this.index != null && DateTime.UtcNow < this.retryAfter)
            {
                return this.index;
            }

            await this.gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (this.index != null && DateTime.UtcNow < this.retryAfter)
                {
                    return this.index;
                }

                this.retryAfter = DateTime.MaxValue;
                var cache = Path.Combine(this.dataDir(), "kometa-images.json");
                if (File.Exists(cache) && File.GetLastWriteTimeUtc(cache) > DateTime.UtcNow.AddDays(-7))
                {
                    return this.index = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(cache));
                }

                try
                {
                    var list = await this.FetchIndexAsync(ct).ConfigureAwait(false);
                    File.WriteAllText(cache, JsonSerializer.Serialize(list));
                    return this.index = list;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    // GitHub unreachable, rate-limited or erroring: an older list beats none.
                    Plugin.Instance?.Logger.Warn("Kometa image list not refreshed from GitHub ({0}); using {1}", ex.Message, File.Exists(cache) ? "the cached list" : "the built-in list");
                    var fallback = File.Exists(cache) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(cache)) : BuiltIn();
                    this.index = fallback;
                    this.retryAfter = DateTime.UtcNow.AddHours(6);
                    return fallback;
                }
            }
            finally
            {
                this.gate.Release();
            }
        }

        /// <summary>
        /// The repo is too big for one recursive tree request (GitHub answers 500), so list the
        /// top level and then each poster folder on its own. Folders that fail are skipped.
        /// </summary>
        private async Task<List<string>> FetchIndexAsync(CancellationToken ct)
        {
            var list = new List<string>();
            using (var top = await GetTreeAsync("master", false, ct).ConfigureAwait(false))
            {
                var folders = top.RootElement.GetProperty("tree").EnumerateArray()
                    .Where(e => e.GetProperty("type").GetString() == "tree")
                    .Select(e => (path: e.GetProperty("path").GetString(), sha: e.GetProperty("sha").GetString()))
                    .Where(f => Preferred.Any(p => p.StartsWith(f.path + "/", StringComparison.Ordinal)) || f.path == "playlist" || f.path == "based")
                    .ToList();
                var failed = 0;
                foreach (var (path, sha) in folders)
                {
                    try
                    {
                        using var sub = await GetTreeAsync(sha, true, ct).ConfigureAwait(false);
                        foreach (var e in sub.RootElement.GetProperty("tree").EnumerateArray())
                        {
                            var p = path + "/" + e.GetProperty("path").GetString();
                            if (e.GetProperty("type").GetString() == "blob" && IsPoster(p))
                            {
                                list.Add(p);
                            }
                        }
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        failed++;
                        Plugin.Instance?.Logger.Warn("Kometa folder {0} not listed: {1}", path, ex.Message);
                    }
                }

                if (list.Count == 0 || failed > folders.Count / 2)
                {
                    throw new InvalidOperationException($"{failed} of {folders.Count} Kometa folders could not be listed");
                }
            }

            return list;
        }

        private static async Task<JsonDocument> GetTreeAsync(string sha, bool recursive, CancellationToken ct)
        {
            var url = "https://api.github.com/repos/Kometa-Team/Default-Images/git/trees/" + sha + (recursive ? "?recursive=1" : string.Empty);
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.Client.SendAsync(req, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }

        /// <summary>Poster list shipped with the plugin (taken from Default-Images when this version was built).</summary>
        private static List<string> BuiltIn()
        {
            using var raw = typeof(KometaArtworkProvider).Assembly.GetManifestResourceStream("EmbyCollections.kometa-images.txt.gz");
            if (raw == null)
            {
                return new List<string>();
            }

            using var gz = new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionMode.Decompress);
            using var reader = new StreamReader(gz);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(IsPoster).ToList();
        }

        private static bool IsPoster(string p)
        {
            return (p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                && !p.Contains("/overlays/", StringComparison.Ordinal)
                && !p.Contains("/logos", StringComparison.Ordinal)
                && !p.Contains("/white/", StringComparison.Ordinal)
                && !p.StartsWith("separators/", StringComparison.Ordinal)
                && !Path.GetFileName(p).StartsWith("!_", StringComparison.Ordinal);
        }
    }
}
