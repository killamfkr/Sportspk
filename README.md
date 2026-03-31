# Sportspk — Jellyfin Live Matches

Jellyfin **10.11+** channel plugin that mirrors a PlayTorrio-style **Live Matches** experience:

- **Streamed.pk** — Live / Today / All, sport tabs, match sources, embed → direct stream resolution when possible  
- **PPV.to** — category folders, iframe-based streams  
- **CDN Live** — online channels and sports events by tournament  

## Requirements

- [.NET SDK 9](https://dotnet.microsoft.com/download) (see `global.json` for the pinned feature band)
- Jellyfin server **10.11.x** (matches `Jellyfin.Model` / `Jellyfin.Controller` package version in the `.csproj`)

## Build

```bash
dotnet build -c Release
```

Output: `bin/Release/net9.0/Jellyfin.Plugin.StreamedPk.dll` and `meta.json`.

## Install from the plugin catalog (recommended)

1. In Jellyfin: **Dashboard → Plugins → Repositories** (or **Catalog** → repository list, depending on version).  
2. **Add** a repository and paste this URL (must be the **raw** `manifest.json`):

   ```
   https://raw.githubusercontent.com/killamfkr/Sportspk/main/manifest.json
   ```

3. Save, open the plugin catalog, find **Live Matches**, and install.  
4. Restart Jellyfin when prompted.

The catalog entry points at a **GitHub Release** zip. Until [release `v1.3.0`](https://github.com/killamfkr/Sportspk/releases/tag/v1.3.0) exists with asset `live-matches_1.3.0.0.zip`, installation from the catalog will fail. Publish a release using either method below.

### Publishing `v1.3.0` (pick one)

- **Git tag (CI):** push tag `v1.3.0`. The [Release workflow](.github/workflows/release.yml) builds on Ubuntu, uploads `live-matches_1.3.0.0.zip`, and prints the zip **MD5** in the job summary. If that MD5 is **not** `17dea3fbf0f90c695c5973ee2dfa54ae`, edit `manifest.json` → `versions[0].checksum`, commit, and push so it matches the release zip.  
- **Manual (Windows):** run `.\scripts\build-plugin-package.ps1`, then create GitHub Release `v1.3.0` and upload `artifacts\live-matches_1.3.0.0.zip`. That matches the checksum currently committed in `manifest.json` if you do not change the build outputs.

## Manual install (no catalog)

1. Copy `Jellyfin.Plugin.StreamedPk.dll` and `meta.json` into your Jellyfin plugins directory (e.g. `plugins/` under the Jellyfin data folder).  
2. Restart Jellyfin.  
3. Enable **Live Matches** under **Dashboard → Plugins** and configure feed URLs / provider toggles if needed.

## Configuration

Plugin settings (Dashboard → Plugins → Live Matches):

- Enable the channel and each provider (Streamed.pk, PPV.to, CDN Live)  
- Streamed.pk API base URL  
- PPV and CDN feed URLs (defaults match the public endpoints used by similar apps)  
- Optional: resolve embed/player pages to direct HLS/DASH URLs before playback  

## Playback (important)

Jellyfin plays these feeds by **pulling the stream on the server** (often **remux/transcode** to HLS for the app). That means:

- **Video transcoding** should be allowed for the user, and **FFmpeg** must work on the server.  
- Many CDNs only allow browser-like requests; this plugin sends **Referer** (the original embed/player URL) and a **Chrome User-Agent** on the stream URL.  
- Feeds that only load the real URL inside **opaque JavaScript**, or use **DRM**, will **not** work without a real browser player.

## Disclaimer

Third-party APIs and streams are outside this project. You are responsible for complying with their terms and for rights to any content you play.

## Repository

https://github.com/killamfkr/Sportspk
