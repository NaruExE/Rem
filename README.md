# REM

> *"Who's Rem?"* — Chrome, after eating 9 GB of your RAM just to render two tabs and forgetting everything else exists.

**REM (RAM Efficient Manager)** is a modular desktop hub built in C# / .NET 8 + Avalonia UI because opening Chrome, Spotify, and three Electron apps just to listen to music and watch a stream shouldn't put a 16 GB PC on life support.

If a module isn't open, it's dead (`0` processes, `~40 MB` idle RAM). Run it normally, kick it to **Headless** when you're in-game and your RAM or internet is fighting for its life, or hit **Kill** to nuke it straight back to baseline.

---

## Modules

- **Audio Streamer (YT / Twitch)** — Audio-only YouTube (For You / Subscriptions via your local browser cookies, topics, search) and Twitch (categories, Following live) through LibVLC. Zero video bandwidth wasted unless you actually want it.
- **Stream Watcher (YT / Twitch)** — Dedicated LibVLC video player for YouTube & Twitch with quality presets (`Auto` down to `240p` for metered connections), speed, snapshots, headless Audio-Only / Keep-Video toggle, and a live **Twitch Chat** sidebar with **Read + Send + Twitch & 7TV emotes** (all individually toggleable so chat doesn't eat RAM for no reason).
- **Video Player (VLC)** — Hardware-decoded local & URL player with playlists, external `.srt`/`.ass`/`.vtt` subtitles (auto-fixes crusty Windows-1252/1256 encoding + delay sync), A-B loop, frame step, aspect ratio cycling, and snapshots.
- **Movies & TV** — TMDB browser, watchlist, and continue-watching history backed by a lazy-loaded, locked-down WebView2 host. Blocks popups, redirects, and ads, and kills `msedgewebview2.exe` the second you hit Back or Kill.
- **Spotify** — Runs on Spotify's official headless `soloist.exe` daemon with synced lyrics, playback controls, and a **Kill** button that actually closes it instead of leaving 6 background processes alive.
- **Calendar & To-Do** — Because apparently we still have to get real life shit done.

---

## Setup

### Requirements
- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Build & Run
```powershell
git clone [https://github.com/NaruExE/Rem.git](https://github.com/NaruExE/Rem.git)
cd Rem
dotnet run --project REM/REM.csproj -c Release
```

NuGet handles Avalonia, LibVLC, SQLite, NAudio, WebView2 SDK, and YoutubeExplode automatically — none of that is bloated into the repo.

### External Tools (not bundled in git)
- **`yt-dlp.exe`** — Handled automatically. First time YouTube/Twitch/Video playback needs it, REM pulls the latest official release from GitHub into `%LocalAppData%\REM\Tools\yt-dlp.exe`.
- **`soloist.exe`** *(optional, Spotify module only)* — Spotify locks Soloist behind their Developer Portal and expires binaries every 90 days, so REM can't auto-fetch it. Grab `soloist_release_windows_x86_64.zip` from the Spotify Developer Dashboard and drop `soloist.exe` into `REM\Tools\` or `%LocalAppData%\REM\Tools\`.
- **WebView2 Runtime** — Already pre-installed on Windows 10/11.

---

## Data & Privacy

Nothing personal touches this repo. Your API keys, OAuth tokens, profiles, playlists, watchlists, and logs stay strictly local:
- `%LocalAppData%\REM\rem.db` (SQLite database)
- `%LocalAppData%\REM\Logs\`
- `%LocalAppData%\REM\Tools\`

YouTube feeds read cookies directly from your local browser profile — nothing is copied, stored, or sent anywhere.

---

Now close Chrome and go touch grass.
