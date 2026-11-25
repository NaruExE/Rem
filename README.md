# REM

> *"Who's Rem?"* — Chrome, after eating 9 GB of your RAM just to render two tabs and forgetting everything else exists.

**REM (RAM Efficient Manager)** is a modular Windows desktop hub built in C# / .NET 8 + Avalonia UI because opening Chrome, Spotify, and three Electron apps just to listen to music, watch a stream, and grab/compress media shouldn't put a 16 GB PC on life support.

If a module isn't open, it's dead (`0` processes, `~23–40 MB` idle private RAM). Run it normally, kick it to **Headless** (with a fixed bottom-right **Mini-Player Dock** for Spotify / Audio Streamer / VLC) when you're in-game and your RAM or internet is fighting for its life, or hit **Kill** to nuke it straight back to baseline.

---

## Modules

- **Audio Streamer (YT / Twitch)** — Audio-only YouTube (For You / Subscriptions via your local browser cookies, topics, search) and Twitch (categories, Following live) through LibVLC. Zero video bandwidth wasted unless you actually want it.
- **Stream Watcher (YT / Twitch)** — Dedicated LibVLC video player for YouTube & Twitch with quality presets (`Auto` down to `240p` for metered connections), speed, snapshots, headless Audio-Only / Keep-Video toggle, and a live **Twitch Chat** sidebar with **Read + Send + Twitch & 7TV emotes** (all individually toggleable so chat doesn't eat RAM for no reason).
- **Media Grabber** — Unified download queue routing video/stream URLs to `yt-dlp.exe` and direct HTTP / `magnet:?` links to lazy `aria2c.exe` JSON-RPC, with presets, background headless queueing, Reveal in Explorer, and 1-click Play in VLC.
- **Compress & Convert (FFmpeg Studio)** — Lazy `ffmpeg.exe` + `ffprobe.exe` toolkit for **Discord Target Size** (`8/10/25/50 MB` or custom), **Trim / Cut** (fast lossless `-c copy` or re-encode), **Video** (`MP4`, `MKV`, `WebM`, `GIF`), **Audio / Track Extraction** (`MP3`, `M4A`, `WAV`, `FLAC`, `OPUS`), and **Images** (`WebP`, `PNG`, `JPG` with resize & metadata strip).
- **Watch-Party Sync** — Sync VLC playback (local files or shared YouTube/stream URLs) + built-in Party Chat behind CGNAT with zero router port-forwarding via auto-downloaded **Cloudflare Quick Tunnels** or **Direct LAN / Radmin IP**, secured with random URL session tokens, optional Room PIN rate-limiting, and Host-Only playback controls.
- **Video Player (VLC)** — Hardware-decoded local & URL player with playlists, "Add Folder as Playlist", external `.srt`/`.ass`/`.vtt` subtitles (auto-fixes crusty Windows-1252/1256 encoding + delay sync), A-B loop, frame step, aspect ratio cycling, and snapshots (automatically unloads idle native LibVLC after 10 minutes).
- **Movies & TV** — TMDB browser, watchlist, and continue-watching history backed by a lazy-loaded, locked-down WebView2 host. Blocks popups, redirects, and ads, and kills `msedgewebview2.exe` the second you hit Back or Kill.
- **Spotify** — Runs on Spotify's official headless `soloist.exe` daemon + Web API PKCE + LRCLIB synced lyrics + Last.fm mixes, with a **Kill** button that actually closes it instead of leaving 6 background processes alive.

Custom **Dashboard Wallpapers**, adjustable **Module Surface Transparency**, and drop-in `.axaml` themes are built into Settings.

---

## Setup

### Requirements
- Windows 10 / 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Build & Run
```powershell
git clone https://github.com/NaruExE/Rem.git
cd Rem
dotnet run --project REM/REM.csproj -c Release
```

NuGet handles Avalonia, LibVLC, SQLite, NAudio, WebView2 SDK, and YoutubeExplode automatically — none of that is bloated into the repo.

### External Tools (not bundled in git)
- **`yt-dlp.exe`, `ffmpeg.exe`, `ffprobe.exe`, `aria2c.exe`, `cloudflared.exe`** — Handled automatically. The first time a module needs one, REM downloads and PE/SHA-256 validates the official release into `%LocalAppData%\REM\Tools\`.
- **`soloist.exe`** *(optional, Spotify module only)* — REM automatically downloads and refreshes `soloist.exe` from Spotify's CDN when needed (before the 90-day build expiry), though you still need your own **Spotify Developer Soloist API Key** (and Client ID for PKCE Web API) in Spotify Settings.
- **WebView2 Runtime** — Already pre-installed on Windows 10/11.

---

## Data & Privacy

Nothing personal touches this repo. Your profiles, playlists, watchlists, and logs stay strictly local under:
- `%LocalAppData%\REM\rem.db` (SQLite database — API secrets, client secrets, and OAuth tokens are encrypted via **Windows DPAPI** `CurrentUser` scope)
- `%LocalAppData%\REM\Logs\`
- `%LocalAppData%\REM\Tools\`

YouTube feeds read cookies directly from your local browser profile — nothing is copied, stored, or sent anywhere.

---

Now close Chrome and go touch grass.
