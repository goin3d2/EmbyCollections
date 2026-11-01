using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbyCollections.Model;

namespace EmbyCollections.Sources
{
    public enum MediaKind
    {
        Any,
        Movie,
        Show,
    }

    /// <summary>One title from a source, by whatever ids it has. Path is for sources that know the exact file/folder.</summary>
    public sealed class MediaRef
    {
        public MediaKind Kind { get; set; }

        public string Imdb { get; set; }

        public string Tmdb { get; set; }

        public string Tvdb { get; set; }

        public string Path { get; set; }

        public string Title { get; set; }

        public int Rank { get; set; }
    }

    public sealed class SourceResult
    {
        public List<MediaRef> Refs { get; } = new List<MediaRef>();

        /// <summary>Library items the source found directly (no matching needed).</summary>
        public List<long> ItemIds { get; } = new List<long>();

        public List<string> Warnings { get; } = new List<string>();

        /// <summary>What the source did, for the activity log.</summary>
        public List<string> Notes { get; } = new List<string>();
    }

    public interface ICollectionSource
    {
        SourceType Type { get; }

        Task<SourceResult> ResolveAsync(SourceDefinition source, CancellationToken cancellationToken);
    }

    internal static class Http
    {
        public static HttpClient Client { get; set; } = Create();

        private static HttpClient Create()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("EmbyCollections/0.1");
            return client;
        }
    }
}
