# Rem

> *"Who's Rem?"* -- Chrome, after forgetting she exists because Chrome already ate 9 GB of your RAM just to render two tabs.

**REM (RAM Efficient Manager)** is a desktop hub built in C# / .NET 8 + Avalonia UI because opening Chrome, Edge, Spotify, and three Electron apps just to listen to music, watch a Twitch stream, and keep track of shit shouldn't require 16 GB of memory (╯°□°）╯︵ ┻━┻

If a module isn't open, it's dead (`0` processes, `~40 MB` private RAM). Run stuff in UI, kick it to Headless audio-only when your internet or RAM is dying, or hit **Kill** to nuke it straight back to baseline >:3

---

## Which fuckery are we up to (Modules)

- **Audio Streamer (YT/Twitch)** -- Audio-only YouTube (For You / Subscriptions via your browser cookies, topics, search) and Twitch (categories, Following live) through LibVLC, zero video bandwidth wasted unless you ask for it.
- **Stream Watcher (YT/Twitch)** -- Full YouTube & Twitch video watching inside a dedicated LibVLC player with quality presets (`Auto` down to `240p` for metered connections), speed, snapshots, headless Audio-Only / Keep-Video chooser, and a live **Twitch Chat** sidebar with **Read + Send + Twitch & 7TV emotes** (all individually toggleable on/off to save RAM).
- **Video Player (VLC)** -- Hardware-decoded local & URL video player with playlists, external `.srt`/`.ass`/`.vtt` subtitles (auto-fixes crusty Windows-1252/1256 encoding + delay sync), A-B loop, frame step, aspect ratio cycling, and snapshots.
- **Movies & TV** -- TMDB browser, watchlist, and continue-watching history backed by a lazy-loaded, locked-down WebView2 host (blocks popups, redirects, and ads; slaughters `msedgewebview2.exe` the second you hit Back/Kill).
- **Spotify** -- Driven through Spotify's headless `soloist.exe` daemon with synced lyrics, playback control, and **Kill** to actually stop it when you're done.
- **Calendar & To-Do List** -- Because apparently we still have to get real life shit done ¯\_(ツ)_/¯

---

## How to run this thing without exploding

### You need:
- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Build & run:
```powershell
git clone https://github.com/NaruExE/Rem.git
cd Rem
dotnet run --project REM/REM.csproj -c Release
```

NuGet pulls Avalonia, LibVLC, SQLite, NAudio, WebView2 SDK, and YoutubeExplode on its own — none of those libraries are dumped in this repo.

### External tools (why they aren't in git)
- **`yt-dlp.exe`** -- You don't have to download it yourself anymore (ง •̀_•́)ง The first time YouTube/Twitch/Video playback needs it, REM quietly grabs the latest official `yt-dlp.exe` from GitHub into `%LocalAppData%\REM\Tools\yt-dlp.exe`.
- **`soloist.exe`** (optional, Spotify module only) -- Spotify locks Soloist behind their Developer Portal login + a 90-day binary expiry, so REM can't fetch it automatically o_o If you want the Spotify module, grab `soloist_release_windows_x86_64.zip` from your Spotify Developer Dashboard and drop `soloist.exe` into `REM\Tools\` or `%LocalAppData%\REM\Tools\`.
- **WebView2 Runtime** -- Already pre-installed on pretty much every Windows 10/11 PC.

---

## Where your actual data lives (chill, nothing personal is in this repo)

All your API keys, OAuth tokens, profiles, playlists, watchlists, and logs stay strictly on your own machine under:
- `%LocalAppData%\REM\rem.db` (SQLite database)
- `%LocalAppData%\REM\Logs\`
- `%LocalAppData%\REM\Tools\`

YouTube account feeds read cookies live from your local browser profile — nothing is copied or uploaded anywhere.

---

now leave me alone so i can go back to sleep (－_－) zzZ
