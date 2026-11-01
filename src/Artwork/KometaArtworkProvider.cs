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
        private const string TreeUrl = "https://api.github.com/repos/Kometa-Team/Default-Images/git/trees/master?recursive=1";

        /// <summary>Folders searched by name, best first. Only full posters (no overlays, logos or grids).</summary>
        private static readonly string[] Preferred =
        {
            "streaming/color/", "network/color/", "studio/", "franchise/", "universe/", "genre/", "seasonal/",
            "decade/", "year/", "country/color/", "chart/color/", "content_rating/", "award/",
        };

        private readonly Func<string> dataDir;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private List<string> index;

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
            var q = Norm(query);
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
                .Select(x => (x.p, score: x.stem == q ? 0 : x.stem.StartsWith(q, StringComparison.Ordinal) ? 1 : x.stem.Contains(q, StringComparison.Ordinal) ? 2 : -1))
                .Where(x => x.score >= 0)
                .OrderBy(x => x.score)
                .ThenBy(x => Rank(x.p))
                .ThenBy(x => x.p.Length)
                .Select(x => x.p);
            return scored.Take(Math.Max(1, limit)).ToList();
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
            if (this.index != null)
            {
                return this.index;
            }

            await this.gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (this.index != null)
                {
                    return this.index;
                }

                var cache = Path.Combine(this.dataDir(), "kometa-images.json");
                if (File.Exists(cache) && File.GetLastWriteTimeUtc(cache) > DateTime.UtcNow.AddDays(-7))
                {
                    return this.index = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(cache));
                }

                try
                {
                    using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, TreeUrl);
                    req.Headers.Accept.ParseAdd("application/vnd.github+json");
                    using var response = await Http.Client.SendAsync(req, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    var list = new List<string>();
                    foreach (var e in doc.RootElement.GetProperty("tree").EnumerateArray())
                    {
                        var p = e.GetProperty("path").GetString();
                        if (e.GetProperty("type").GetString() == "blob" && IsPoster(p))
                        {
                            list.Add(p);
                        }
                    }

                    File.WriteAllText(cache, JsonSerializer.Serialize(list));
                    return this.index = list;
                }
                catch when (File.Exists(cache))
                {
                    // GitHub unreachable or rate-limited: an old index beats none.
                    return this.index = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(cache));
                }
            }
            finally
            {
                this.gate.Release();
            }
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
