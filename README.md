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

## Install

1. Copy `Jellyfin.Plugin.StreamedPk.dll` and `meta.json` into your Jellyfin plugins directory (e.g. `plugins/` under the Jellyfin data folder).  
2. Restart Jellyfin.  
3. Enable **Live Matches** under **Dashboard → Plugins** and configure feed URLs / provider toggles if needed.

## Configuration

Plugin settings (Dashboard → Plugins → Live Matches):

- Enable the channel and each provider (Streamed.pk, PPV.to, CDN Live)  
- Streamed.pk API base URL  
- PPV and CDN feed URLs (defaults match the public endpoints used by similar apps)  
- Optional: resolve embed/player pages to direct HLS/DASH URLs before playback  

## Disclaimer

Third-party APIs and streams are outside this project. You are responsible for complying with their terms and for rights to any content you play.

## Repository

https://github.com/killamfkr/Sportspk
