using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbyCollections.Model;
using EmbyCollections.Sources;

namespace EmbyCollections.Artwork
{
    /// <summary>Just the TMDB calls artwork needs: collection posters and network/company logos.</summary>
    public sealed class TmdbClient
    {
        private const string Api = "https://api.themoviedb.org/3/";
        private const string Images = "https://image.tmdb.org/t/p/original";

        /// <summary>Streaming networks people most often name. TMDB has no network search, so these skip the company lookup.</summary>
        private static readonly Dictionary<string, int> KnownNetworks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["netflix"] = 213,
            ["hbo"] = 49,
            ["hulu"] = 453,
            ["amazon"] = 1024,
            ["prime video"] = 1024,
            ["amazon prime video"] = 1024,
            ["apple tv+"] = 2552,
            ["apple tv"] = 2552,
            ["disney+"] = 2739,
        };

        private readonly string key;

        public TmdbClient(string key)
        {
            this.key = (key ?? string.Empty).Trim();
        }

        public bool IsConfigured => this.key.Length > 0;

        public async Task<string> CollectionPosterUrlAsync(long collectionId, CancellationToken ct)
        {
            using var doc = await this.GetAsync($"collection/{collectionId}", ct).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("poster_path", out var p) && p.ValueKind == JsonValueKind.String
                ? Images + p.GetString()
                : null;
        }

        /// <summary>
        /// Logo URL for "network:213", "company:420", a known network name, or a company name search.
        /// </summary>
        public async Task<string> LogoUrlAsync(string query, CancellationToken ct)
        {
            query = (query ?? string.Empty).Trim();
            if (query.Length == 0)
            {
                return null;
            }

            string path = null;
            if (query.StartsWith("network:", StringComparison.OrdinalIgnoreCase))
            {
                path = "network/" + query.Substring(8).Trim();
            }
            else if (query.StartsWith("company:", StringComparison.OrdinalIgnoreCase))
            {
                path = "company/" + query.Substring(8).Trim();
            }
            else if (KnownNetworks.TryGetValue(query, out var nid))
            {
                path = "network/" + nid;
            }

            if (path != null)
            {
                using var doc = await this.GetAsync(path, ct).ConfigureAwait(false);
                return Logo(doc.RootElement);
            }

            using var search = await this.GetAsync("search/company?query=" + Uri.EscapeDataString(query), ct).ConfigureAwait(false);
            foreach (var r in search.RootElement.GetProperty("results").EnumerateArray())
            {
                var url = Logo(r);
                if (url != null)
                {
                    return url;
                }
            }

            return null;
        }

        private static string Logo(JsonElement e)
        {
            return e.TryGetProperty("logo_path", out var l) && l.ValueKind == JsonValueKind.String ? Images + l.GetString() : null;
        }

        private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
        {
            if (!this.IsConfigured)
            {
                throw new InvalidOperationException("TMDB API key is not set");
            }

            var sep = path.Contains('?') ? "&" : "?";
            using var response = await Http.Client.GetAsync($"{Api}{path}{sep}api_key={Uri.EscapeDataString(this.key)}", ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
    }

    public sealed class TmdbCollectionArtworkProvider : IArtworkProvider
    {
        public ArtworkMode Mode => ArtworkMode.TmdbCollection;

        public async Task<ArtworkImage> GetAsync(ArtworkContext context, CancellationToken ct)
        {
            var id = context.Collection.Artwork.TmdbCollectionId;
            var tmdb = new TmdbClient(context.Settings.TmdbApiKey);
            if (id <= 0 || !tmdb.IsConfigured)
            {
                return null;
            }

            var url = await tmdb.CollectionPosterUrlAsync(id, ct).ConfigureAwait(false);
            return url == null ? null : await ArtworkHttp.DownloadAsync(url, "tmdb-collection", ct).ConfigureAwait(false);
        }
    }
}
