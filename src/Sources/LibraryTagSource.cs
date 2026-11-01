using System.Threading;
using System.Threading.Tasks;
using EmbyCollections.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace EmbyCollections.Sources
{
    /// <summary>Movies and series carrying an Emby tag (e.g. tags written by another plugin or .nfo files).</summary>
    public sealed class LibraryTagSource : ICollectionSource
    {
        private readonly ILibraryManager libraryManager;

        public LibraryTagSource(ILibraryManager libraryManager)
        {
            this.libraryManager = libraryManager;
        }

        public SourceType Type => SourceType.LibraryTag;

        public Task<SourceResult> ResolveAsync(SourceDefinition source, CancellationToken cancellationToken)
        {
            var result = new SourceResult();
            var ids = this.libraryManager.GetInternalItemIds(
                new InternalItemsQuery
                {
                    Recursive = true,
                    IncludeItemTypes = new[] { "Movie", "Series" },
                    Tags = new[] { source.Value.Trim() },
                },
                cancellationToken);
            result.ItemIds.AddRange(ids);
            result.Notes.Add($"Library tag '{source.Value.Trim()}': {ids.Length} movies/series");
            return Task.FromResult(result);
        }
    }
}
