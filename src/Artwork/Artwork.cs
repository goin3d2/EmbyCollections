using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbyCollections.Model;
using EmbyCollections.Sources;

namespace EmbyCollections.Artwork
{
    /// <summary>A poster ready to save. Key identifies it so unchanged artwork isn't re-saved every sync.</summary>
    public sealed class ArtworkImage
    {
        public string Key { get; set; }

        public string Source { get; set; }

        public string MimeType { get; set; } = "image/jpeg";

        public byte[] Data { get; set; }
    }

    public sealed class ArtworkContext
    {
        public CollectionDefinition Collection { get; set; }

        /// <summary>Local primary-image paths of members, in rank order (for mosaics).</summary>
        public IReadOnlyList<string> MemberPosters { get; set; } = Array.Empty<string>();

        public Settings Settings { get; set; }
    }

    public interface IArtworkProvider
    {
        ArtworkMode Mode { get; }

        /// <summary>Returns null when this provider has nothing for the collection.</summary>
        Task<ArtworkImage> GetAsync(ArtworkContext context, CancellationToken cancellationToken);
    }

    /// <summary>Picks the poster: the chosen mode, or for Auto the first provider that has one.</summary>
    public sealed class ArtworkResolver
    {
        private static readonly ArtworkMode[] AutoOrder =
        {
            ArtworkMode.Url, ArtworkMode.Kometa, ArtworkMode.TmdbCollection, ArtworkMode.Generated,
        };

        private readonly Dictionary<ArtworkMode, IArtworkProvider> providers = new Dictionary<ArtworkMode, IArtworkProvider>();

        public ArtworkResolver(IEnumerable<IArtworkProvider> providers)
        {
            foreach (var p in providers)
            {
                this.providers[p.Mode] = p;
            }
        }

        public async Task<ArtworkImage> ResolveAsync(ArtworkContext context, List<string> warnings, CancellationToken ct, Action<string> trace = null)
        {
            var mode = context.Collection.Artwork.Mode;
            if (mode == ArtworkMode.None)
            {
                trace?.Invoke("Artwork set to 'Leave as is': poster not touched");
                return null;
            }

            foreach (var m in mode == ArtworkMode.Auto ? AutoOrder : new[] { mode })
            {
                if (!this.providers.TryGetValue(m, out var provider))
                {
                    continue;
                }

                try
                {
                    var image = await provider.GetAsync(context, ct).ConfigureAwait(false);
                    if (image?.Data != null && image.Data.Length > 0)
                    {
                        trace?.Invoke($"Artwork: {m} → {image.Source} ({image.Data.Length / 1024} KB)");
                        return image;
                    }

                    trace?.Invoke($"Artwork: {m} has nothing for this collection" + (mode == ArtworkMode.Auto ? ", trying the next option" : string.Empty));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Artwork ({m}): {ex.Message}");
                    trace?.Invoke($"Artwork: {m} failed: {ex.Message}");
                }
            }

            return null;
        }
    }

    internal static class ArtworkHttp
    {
        public static async Task<ArtworkImage> DownloadAsync(string url, string source, CancellationToken ct)
        {
            using var response = await Http.Client.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var mime = response.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrEmpty(mime) || !mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                mime = url.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
                    : url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp" : "image/jpeg";
            }

            return new ArtworkImage { Key = source + "|" + url, Source = source, MimeType = mime, Data = data };
        }

        public static string Hash(string text)
        {
            return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)), 0, 8);
        }
    }

    /// <summary>A poster URL the user pasted (ThePosterDB, MediUX, anything).</summary>
    public sealed class UrlArtworkProvider : IArtworkProvider
    {
        public ArtworkMode Mode => ArtworkMode.Url;

        public Task<ArtworkImage> GetAsync(ArtworkContext context, CancellationToken ct)
        {
            var url = context.Collection.Artwork.Url?.Trim();
            if (string.IsNullOrEmpty(url))
            {
                return Task.FromResult<ArtworkImage>(null);
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            {
                throw new ArgumentException("Poster URL must be an http(s) link to an image");
            }

            return ArtworkHttp.DownloadAsync(url, "url", ct);
        }
    }
}
