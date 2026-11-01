using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbyCollections.Model
{
    /// <summary>Everything the plugin stores: API keys and the collection definitions.</summary>
    public class Settings
    {
        public string MdbListApiKey { get; set; } = string.Empty;

        /// <summary>Used for TMDB collection artwork and network/company logos on generated posters.</summary>
        public string TmdbApiKey { get; set; } = string.Empty;

        /// <summary>Folder holding xtreamfs-emby's categories.json. Empty: Emby's data folder + /xtreamfs.</summary>
        public string XtreamFsDataPath { get; set; } = string.Empty;

        /// <summary>Remove members that no longer appear in any source.</summary>
        public bool RemoveMissing { get; set; } = true;

        public List<CollectionDefinition> Collections { get; set; } = new List<CollectionDefinition>();

        public Settings Normalize()
        {
            this.MdbListApiKey = (this.MdbListApiKey ?? string.Empty).Trim();
            this.TmdbApiKey = (this.TmdbApiKey ?? string.Empty).Trim();
            this.XtreamFsDataPath = (this.XtreamFsDataPath ?? string.Empty).Trim();
            this.Collections ??= new List<CollectionDefinition>();
            this.Collections = this.Collections.Where(c => c != null && !string.IsNullOrWhiteSpace(c.Name)).ToList();
            foreach (var c in this.Collections)
            {
                c.Normalize();
            }

            return this;
        }
    }

    public class CollectionDefinition
    {
        /// <summary>Stable id; the BoxSet is tagged with it so the plugin can find its own collections.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; }

        public bool Enabled { get; set; } = true;

        public string SortName { get; set; } = string.Empty;

        public string Overview { get; set; } = string.Empty;

        /// <summary>How sources combine. v1: Union only (Intersect/Exclude are planned).</summary>
        public CombineMode Combine { get; set; } = CombineMode.Union;

        public List<SourceDefinition> Sources { get; set; } = new List<SourceDefinition>();

        public ArtworkDefinition Artwork { get; set; } = new ArtworkDefinition();

        /// <summary>Emby internal id of the BoxSet this plugin manages; 0 until first sync.</summary>
        public long BoxSetId { get; set; }

        public CollectionStatus LastSync { get; set; }

        public string Tag => "embycollections:" + this.Id;

        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(this.Id))
            {
                this.Id = Guid.NewGuid().ToString("N");
            }

            this.Name = this.Name.Trim();
            this.Sources ??= new List<SourceDefinition>();
            this.Sources = this.Sources.Where(s => s != null && !string.IsNullOrWhiteSpace(s.Value)).ToList();
            this.Artwork ??= new ArtworkDefinition();
        }
    }

    public enum CombineMode
    {
        Union,
    }

    public enum SourceType
    {
        /// <summary>An MDBList list: URL, "user/slug" or numeric id. IMDb lists go through MDBList's external lists.</summary>
        MdbList,

        /// <summary>An xtreamfs provider category: "movie:&lt;id&gt;" or "series:&lt;id&gt;".</summary>
        XtreamCategory,

        /// <summary>Library items carrying an Emby tag.</summary>
        LibraryTag,
    }

    public class SourceDefinition
    {
        public SourceType Type { get; set; }

        public string Value { get; set; }

        /// <summary>Optional label shown in the UI (e.g. the category name).</summary>
        public string Label { get; set; } = string.Empty;
    }

    public enum ArtworkMode
    {
        /// <summary>Try URL, then Kometa default image, then TMDB collection, then generate.</summary>
        Auto,
        Url,
        Kometa,
        TmdbCollection,
        Generated,
        /// <summary>Leave whatever image the collection has.</summary>
        None,
    }

    public enum GeneratedStyle
    {
        /// <summary>Collection title on a gradient (with a TMDB logo when LogoQuery is set).</summary>
        Title,

        /// <summary>Grid of member posters with the title across the bottom.</summary>
        Mosaic,
    }

    public class ArtworkDefinition
    {
        public ArtworkMode Mode { get; set; } = ArtworkMode.Auto;

        /// <summary>Direct poster URL (ThePosterDB, MediUX, anywhere).</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>Path inside Kometa-Team/Default-Images, e.g. "streaming/color/Netflix.jpg". Empty: match by name.</summary>
        public string KometaPath { get; set; } = string.Empty;

        public long TmdbCollectionId { get; set; }

        public GeneratedStyle Style { get; set; } = GeneratedStyle.Title;

        /// <summary>TMDB network/company name whose logo goes on a generated poster (e.g. "Netflix").</summary>
        public string LogoQuery { get; set; } = string.Empty;

        /// <summary>Two hex colors for the generated gradient.</summary>
        public string ColorTop { get; set; } = "#1f2a44";

        public string ColorBottom { get; set; } = "#0b0f19";

        /// <summary>What was last applied, so artwork is only re-saved when it changes.</summary>
        public string AppliedKey { get; set; } = string.Empty;
    }

    public class CollectionStatus
    {
        public DateTime Time { get; set; }

        public int SourceItems { get; set; }

        public int Matched { get; set; }

        public int Added { get; set; }

        public int Removed { get; set; }

        public string ArtworkSource { get; set; } = string.Empty;

        public List<string> Warnings { get; set; } = new List<string>();

        public string Error { get; set; }
    }
}
