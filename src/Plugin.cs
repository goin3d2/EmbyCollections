using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbyCollections.Model;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;

namespace EmbyCollections
{
    public class Plugin : BasePlugin, IHasWebPages
    {
        public const string PluginName = "Emby Collections";

        public static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static readonly Guid PluginId = new Guid("36cf26f0-22e8-42fd-8e79-458bc4fe91ff");

        private readonly object sync = new object();
        private readonly IApplicationPaths paths;
        private Settings settings;

        public Plugin(ILogManager logManager, IApplicationPaths applicationPaths)
        {
            Instance = this;
            this.paths = applicationPaths;
            this.Logger = logManager.GetLogger(PluginName);
            this.Logger.Info("{0} loaded", PluginName);
        }

        public static Plugin Instance { get; private set; }

        public override string Name => PluginName;

        public override Guid Id => PluginId;

        public override string Description =>
            "Builds and maintains collections from MDBList/IMDb lists, xtreamfs provider categories and library tags, with artwork.";

        public ILogger Logger { get; }

        public string DataDirectory
        {
            get
            {
                var dir = this.DataFolderPath;
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public string XtreamFsDataPath
        {
            get
            {
                var configured = this.Settings.XtreamFsDataPath;
                return string.IsNullOrEmpty(configured) ? Path.Combine(this.paths.DataPath, "xtreamfs") : configured;
            }
        }

        public Settings Settings
        {
            get
            {
                lock (this.sync)
                {
                    return this.settings ??= this.Load();
                }
            }
        }

        private string SettingsPath => Path.Combine(this.DataDirectory, "settings.json");

        /// <summary>Saves settings from the page, keeping sync state (BoxSet ids, status, applied artwork) the page doesn't own.</summary>
        public void SaveFromPage(Settings incoming)
        {
            incoming.Normalize();
            lock (this.sync)
            {
                var old = this.Settings;
                foreach (var c in incoming.Collections)
                {
                    var prev = old.Collections.Find(o => o.Id == c.Id);
                    if (prev == null)
                    {
                        continue;
                    }

                    c.BoxSetId = prev.BoxSetId;
                    c.LastSync = prev.LastSync;
                    c.Artwork.AppliedKey = prev.Artwork?.AppliedKey ?? string.Empty;
                }

                this.Write(incoming);
            }
        }

        /// <summary>Applies a change from the sync task to the current settings and saves.</summary>
        public void Update(Action<Settings> change)
        {
            lock (this.sync)
            {
                var current = this.Settings;
                change(current);
                this.Write(current);
            }
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "embycollections",
                    DisplayName = PluginName,
                    EmbeddedResourcePath = "EmbyCollections.Web.configPage.html",
                    IsMainConfigPage = true,
                },
                new PluginPageInfo
                {
                    Name = "embycollectionsjs",
                    EmbeddedResourcePath = "EmbyCollections.Web.configPage.js",
                },
            };
        }

        private void Write(Settings value)
        {
            value.Normalize();
            var tmp = this.SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
            File.Move(tmp, this.SettingsPath, true);
            this.settings = value;
        }

        private Settings Load()
        {
            try
            {
                if (File.Exists(this.SettingsPath))
                {
                    var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(this.SettingsPath), Json);
                    if (loaded != null)
                    {
                        return loaded.Normalize();
                    }
                }
            }
            catch (Exception ex)
            {
                this.Logger.ErrorException("Could not read {0}; using defaults", ex, this.SettingsPath);
            }

            return new Settings();
        }
    }
}
