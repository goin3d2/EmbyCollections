# Emby Collections

An Emby Server plugin that builds and maintains collections from one or more sources and gives each one a poster.

**Status: v0.1 scaffold.** It builds against Emby 4.11 and the non-Emby logic is tested. It has not been run on a live server yet.

## Sources

Each collection combines any number of sources. In v1 they are combined as a union: a title is in the collection if any source lists it.

| Source | Value | How items are matched |
|---|---|---|
| **MDBList** | List URL (`https://mdblist.com/lists/user/slug`), `user/slug`, list id, or an external-list URL | IMDb, TMDB and TVDB ids |
| **IMDb list** | Add the IMDb list to MDBList as an external list, then use that MDBList URL | Same as MDBList |
| **xtreamfs category** | Picked from the provider's categories (`movie:<id>` / `series:<id>`) | Exact `.strm` / show-folder path, then TMDB id |
| **Library tag** | An Emby tag name | Direct |

TMDB ids are only compared within the same kind (movie or show), because TMDB reuses numbers across them.

The xtreamfs source reads `categories.json`, which xtreamfs-emby writes to `data/xtreamfs/` after each refresh. This needs the xtreamfs-emby version that writes that file.

## Artwork

In **Auto** mode the plugin tries each option in order and uses the first one that has a poster:

1. **Poster URL**: any image link, such as a ThePosterDB or MediUX poster.
2. **Kometa default image**: posters from [Kometa-Team/Default-Images](https://github.com/Kometa-Team/Default-Images), matched by collection name or picked on the settings page. Streaming services, networks, studios, franchises, genres, seasonal and more are covered; "Netflix", for example, matches `streaming/color/Netflix.jpg`. The plugin downloads the file list once a week and fetches images when they're used. Nothing is bundled.
3. **TMDB collection**: the official poster for a TMDB collection id.
4. **Generated**: the plugin draws a poster itself. There are two styles:
   - **Title on a gradient**, optionally under a TMDB network or company logo (`Netflix`, `A24`, `network:213`, `company:41077`).
   - **Mosaic** of member posters with the title across the bottom.

A poster is only saved again when it changes.

## Managing existing collections

The **All collections** tab lists every collection on the server, whether this plugin made it or not. Pick one to:

- Change its name, sort name or overview.
- Set a new poster (Kometa image, URL, uploaded file, generated or TMDB collection) or remove the poster.
- Remove titles from it.
- Delete it. The titles themselves aren't touched.
- **Manage with this plugin.** The collection is adopted as add-only with no sources, so nothing in it changes until you add sources on the Managed tab.

For a managed collection, a poster set by hand switches its artwork to "Leave as is", so the next sync doesn't replace it. Name edits are kept in sync with the plugin's settings too.

## Safety

- Sync only touches collections the plugin created or that you adopted. Each one is tagged `embycollections:<id>`. Other collections change only when you edit them on the All collections tab.
- If any source fails during a sync, nothing is removed from that collection. This stops a failed list fetch from emptying a collection.
- **Stop managing** removes the definition from the plugin and leaves the Emby collection in place.
- **Add only** collections never lose titles, and a collection with no sources is never emptied.

## Install

1. Build the plugin (see below), or download `EmbyCollections.dll` from Releases.
2. Copy it into Emby's `plugins` folder and restart Emby.
3. Open **Dashboard → Emby Collections**. Add an MDBList API key (and a TMDB key if you want logos or TMDB collection posters), then add collections.
4. The **Sync collections** scheduled task runs every 12 hours. Each collection also has a **Save and sync** button.

## Build

```sh
# Copy MediaBrowser.Common/Controller/Model.dll and SkiaSharp.dll from an Emby 4.10+ server's
# system folder (or the release .deb) into ./emby-lib, then:
dotnet build -c Release -o out
```

`nuget.config` clears the package sources, so the build works offline. Pass `-p:EmbyLibDir=/path` to use another folder.

## Planned

- Combine sources with intersect and exclude, not just union.
- Ordering by list rank. Emby BoxSets only sort by premiere date or sort name.
- A missing-items report, and optionally sending missing titles to Radarr/Sonarr.
- Pasting a ThePosterDB or MediUX set URL and having the plugin pick the right poster from the set.

## Credits

Default posters: [Kometa](https://github.com/Kometa-Team/Kometa) Default-Images. Title font: DejaVu Sans Condensed Bold (Bitstream Vera / DejaVu license).
