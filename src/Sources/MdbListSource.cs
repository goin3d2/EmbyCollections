using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EmbyCollections.Model;

namespace EmbyCollections.Sources
{
    /// <summary>
    /// MDBList lists through api.mdblist.com. Accepts a list URL (https://mdblist.com/lists/user/slug),
    /// "user/slug", a numeric list id, or an external list URL (.../lists/external/123) — which is how
    /// IMDb lists come in: add the IMDb list to MDBList as an external list, then use that URL here.
    /// </summary>
    public sealed class MdbListSource : ICollectionSource
    {
        private const string Api = "https://api.mdblist.com/";
        private const int PageSize = 1000;
        private const int MaxPages = 20;

        private static readonly Regex ListUrl = new Regex(@"mdblist\.com/lists/(?<path>[^?#]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly Func<string> apiKey;

        public MdbListSource(Func<string> apiKey)
        {
            this.apiKey = apiKey;
        }

        public SourceType Type => SourceType.MdbList;

        /// <summary>Turns what the user typed into the items endpoint path (without the trailing "items/").</summary>
        public static string ListPath(string value)
        {
            var v = (value ?? string.Empty).Trim();
            var m = ListUrl.Match(v);
            if (m.Success)
            {
                v = m.Groups["path"].Value;
            }

            v = v.Trim('/');
            if (v.StartsWith("external/", StringComparison.OrdinalIgnoreCase))
            {
                return "external/lists/" + v.Substring("external/".Length);
            }

            if (v.Length == 0)
            {
                throw new ArgumentException("Empty MDBList list");
            }

            return "lists/" + v;
        }

        /// <summary>Checks a key against MDBList's user endpoint and reports the daily request quota.</summary>
        public static async Task<(bool Ok, string Message)> TestKeyAsync(string key, CancellationToken ct)
        {
            key = (key ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                return (false, "Enter an MDBList API key first.");
            }

            using var response = await Http.Client.GetAsync($"{Api}user?apikey={Uri.EscapeDataString(key)}", ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                return (false, "MDBList rejected the key. Copy it again from mdblist.com → Preferences → API.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return (false, $"MDBList answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            try
            {
                using var doc = JsonDocument.Parse(text);
                var r = doc.RootElement;
                if (r.TryGetProperty("error", out var err))
                {
                    return (false, "MDBList: " + err.ToString());
                }

                var name = Str(r, "username") ?? Str(r, "name");
                var limit = Str(r, "api_requests");
                var used = Str(r, "api_requests_count") ?? "0";
                var msg = "MDBList key works" + (name != null ? $" (user {name})" : string.Empty) + ".";
                if (limit != null)
                {
                    msg += $" {used} of {limit} requests used today.";
                }

                return (true, msg);
            }
            catch (JsonException)
            {
                return (true, "MDBList key works.");
            }
        }

        public async Task<SourceResult> ResolveAsync(SourceDefinition source, CancellationToken cancellationToken)
        {
            var key = this.apiKey();
            if (string.IsNullOrEmpty(key))
            {
                throw new InvalidOperationException("MDBList API key is not set");
            }

            var result = new SourceResult();
            var path = ListPath(source.Value);
            var rank = 0;
            for (var page = 0; page < MaxPages; page++)
            {
                var url = $"{Api}{path}/items/?apikey={Uri.EscapeDataString(key)}&limit={PageSize}&offset={page * PageSize}&unified=true";
                using var response = await Http.Client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new InvalidOperationException($"MDBList list not found: {source.Value}");
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new InvalidOperationException("MDBList rejected the API key");
                }

                if ((int)response.StatusCode == 429)
                {
                    throw new InvalidOperationException("MDBList rate limit reached; try again later");
                }

                response.EnsureSuccessStatusCode();
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var items = ParseItems(text).ToList();
                foreach (var item in items)
                {
                    item.Rank = ++rank;
                    result.Refs.Add(item);
                }

                var hasMore = response.Headers.TryGetValues("X-Has-More", out var vals)
                    && vals.Any(v => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));
                if (!hasMore || items.Count == 0)
                {
                    break;
                }
            }

            return result;
        }

        /// <summary>Handles both shapes MDBList returns: a plain array, or { movies: [...], shows: [...] } / { items: [...] }.</summary>
        public static IEnumerable<MediaRef> ParseItems(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var arrays = new List<(JsonElement arr, MediaKind kind)>();
            if (root.ValueKind == JsonValueKind.Array)
            {
                arrays.Add((root, MediaKind.Any));
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var (name, kind) in new[] { ("movies", MediaKind.Movie), ("shows", MediaKind.Show), ("items", MediaKind.Any) })
                {
                    if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        arrays.Add((arr, kind));
                    }
                }
            }

            var list = new List<MediaRef>();
            foreach (var (arr, kind) in arrays)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var k = kind;
                    var mt = Str(e, "mediatype") ?? Str(e, "type");
                    if (mt != null)
                    {
                        k = mt.StartsWith("movie", StringComparison.OrdinalIgnoreCase) ? MediaKind.Movie
                            : mt.StartsWith("show", StringComparison.OrdinalIgnoreCase) || mt.StartsWith("tv", StringComparison.OrdinalIgnoreCase) ? MediaKind.Show
                            : k;
                    }

                    e.TryGetProperty("ids", out var ids);
                    var r = new MediaRef
                    {
                        Kind = k,
                        Title = Str(e, "title"),
                        Imdb = Str(e, "imdb_id") ?? Str(e, "imdbid") ?? Str(ids, "imdb"),
                        Tmdb = Str(e, "tmdb_id") ?? Str(e, "tmdbid") ?? Str(ids, "tmdb") ?? Str(e, "id"),
                        Tvdb = Str(e, "tvdb_id") ?? Str(e, "tvdbid") ?? Str(ids, "tvdb"),
                    };
                    if (r.Imdb != null || r.Tmdb != null || r.Tvdb != null)
                    {
                        list.Add(r);
                    }
                }
            }

            return list;
        }

        private static string Str(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            {
                return null;
            }

            var s = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            };
            return string.IsNullOrWhiteSpace(s) || s == "0" ? null : s.Trim();
        }
    }
}
