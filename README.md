# REM

> *"Who's Rem?"*

Are you tired of Chrome, Spotify, Discord, and three Electron apps eating every last gigabyte of your memory alive?

It's okay. **Rem is here to protect her sister, Ram!**

**REM (RAM Efficient Manager)** is a native Windows desktop hub written in C# / .NET 8 + Avalonia UI so you can listen to music, watch streams, download videos, shrink clips, and host watch parties without leaving 9 GB of idle browser tabs rotting in the background.

Every app inside REM has three states you actually control:
- **Open (UI):** Use it normally.
- **Headless:** Closes the heavy window completely while music, downloads, encodes, or Watch Party keep running in the background. Audio apps pop into a tiny fixed **Mini-Player Dock** in the bottom right with prev/next switcher arrows.
- **Kill:** Actually slaughters the module and its child processes back down to `0` (`~23–40 MB` idle private RAM for REM itself, and the top bar shows REM + its own child processes together honestly).

---

## What's Inside

### Works the second you open it (zero API keys needed)
- **Compress & Convert (FFmpeg Studio):** Shrink a 180 MB video under `8 / 10 / 25 / 50 MB` so Discord stops begging you for Nitro, cut clips fast without re-encoding, convert videos (`MP4`, `MKV`, `WebM`, `GIF`), rip audio (`MP3`, `M4A`, `WAV`, `FLAC`, `OPUS`), or convert/strip EXIF from images.
- **Watch-Party Sync:** Watch movies or YouTube links together in VLC with synced play/pause/seek/speed and a built-in room chat. Works even on Egyptian routers / CGNAT with zero port-forwarding using a Cloudflare Quick Tunnel link (or Direct LAN / Radmin IP), locked with a random URL session token, optional PIN rate-limiting, and host-only control toggle.
- **Media Grabber:** Paste a YouTube, Twitch, social, direct file, or `magnet:?` link and let `yt-dlp` / `aria2c` pull it in the background. Reveal in Explorer or play it straight in VLC when it's done.
- **Audio Streamer (YT / Twitch):** Listen to YouTube and Twitch audio-only via LibVLC with zero video bandwidth wasted.
- **Stream Watcher (YT / Twitch):** Watch streams/videos in LibVLC with quality caps (`Best` all the way down to `240p` for metered connections) + a toggleable Twitch Chat sidebar with 7TV emotes.
- **Video Player (VLC):** Local files, folder playlists, A-B loop, frame step, speed, snapshots, auto-fixed crusty Arabic/Windows subtitle encodings (`Windows-1256` / `1252`), and it auto-unloads LibVLC if you leave it idle for 10 minutes.

### Optional modules (need your own free API keys)
- **Spotify:** Headless `soloist.exe` playback + synced lyrics (`LRCLIB`) + playlists + mixes (`Last.fm`). Hits **Kill** -> `soloist.exe` actually dies.
- **Movies & TV:** TMDB catalog + watchlist + continue watching using a lazy WebView2 host that blocks popups/redirects/ad domains and destroys `msedgewebview2.exe` the second you leave or Kill it.

Also includes multi-profile switching, custom **Dashboard Wallpapers** with adjustable **Module Transparency**, and drop-in `.axaml` themes.

---

## How to Get & Run It

### Option 1: Portable ZIP (Recommended — no .NET install needed)
1. Grab **`REM-win-x64.zip`** from the [Latest Release](https://github.com/NaruExE/Rem/releases/latest).
2. Right-click -> **Extract All...** into a folder (don't try running it straight from inside the ZIP).
3. Double-click **`REM.exe`**.

### Option 2: Run from source
Needs Windows 10/11 and [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
```powershell
git clone https://github.com/NaruExE/Rem.git
cd Rem
dotnet run --project REM/REM.csproj -c Release
```

---

## Setup Cheat Sheet (so you don't have to ask me)

Click any dropdown below if you want to set up the optional API modules or YouTube feeds:

<details>
<summary><b>Spotify Setup (Web API + Soloist + Lyrics)</b></summary>

1. Go to the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard) -> Create App -> check **Web API**.
2. Set the **exact** Redirect URI to:
   ```text
   http://127.0.0.1:8888/callback
   ```
3. Copy your **Client ID** into **Spotify Module Settings** in REM and hit **Connect Spotify**.
4. Also grab your **Soloist API Key** from the Spotify Developer Dashboard (Spotify requires Premium for Soloist) and paste it in the same settings page.
   - You don't need to hunt down `soloist.exe` manually anymore — REM auto-downloads and refreshes it into `%LocalAppData%\REM\Tools\soloist.exe` (even when Spotify's 90-day build timer expires).
5. *(Optional)* Grab a free API key from [Last.fm](https://www.last.fm/api/account/create) for better mixes & recommendations. Lyrics come automatically from LRCLIB with no key.

</details>

<details>
<summary><b>Twitch Following & Live Chat Setup</b></summary>

1. Go to the [Twitch Developer Console](https://dev.twitch.tv/console/apps) -> Register Your Application.
2. Set the **exact** OAuth Redirect URL to:
   ```text
   http://localhost:8080/callback
   ```
3. Copy your **Client ID** and **Client Secret** into Audio Streamer / Stream Watcher Module Settings and click **Connect Twitch Account**.

</details>

<details>
<summary><b>Movies & TV Setup (TMDB)</b></summary>

1. Create a free account on [TMDB](https://www.themoviedb.org/settings/api) and copy your **API Read Access Token (Bearer token)** — the long JWT one, not the short v3 key.
2. Paste it into **Movies & TV Module Settings** along with your own movie/TV embed templates (`{tmdb}`, `{imdb}`, `{season}`, `{episode}`). REM ships with blank embed URLs on purpose.

</details>

<details>
<summary><b>YouTube "For You" & Subscriptions Feed</b></summary>

- Normal YouTube search and playback work out of the box.
- If you want your personal home feed and subscriptions in Audio Streamer, pick your browser in settings (`Cookies Browser`).
- **Heads up:** Chrome and Edge on Windows lock cookies behind App-Bound Encryption so `yt-dlp` usually fails on them. **Firefox** and **Zen Browser** work reliably.

</details>

---

## Before You Panic (Quick FAQ)

- **Windows Defender showed "Windows protected your PC"!**
  Because Microsoft wants ~$300/yr for a code-signing certificate and I am not paying that for a free app. Click **More info then Run anyway**, or build it straight from source if you're paranoid
- **Where do `yt-dlp.exe`, `ffmpeg.exe`, `aria2c.exe`, `cloudflared.exe`, and `soloist.exe` come from?**
  REM doesn't bloat the git repo with EXEs. The first time you actually use a feature that needs one, REM downloads the official release, checks the Windows PE header + SHA-256, and drops it into `%LocalAppData%\REM\Tools\`.
- **Where are my keys, playlists, and logs stored?**
  Strictly on your PC under `%LocalAppData%\REM\rem.db` (API tokens & secrets are encrypted with **Windows DPAPI** `CurrentUser` scope), `%LocalAppData%\REM\Logs\`, and `%LocalAppData%\REM\Tools\`. Nothing ever leaves your machine.

---

now close chrome and go touch grass (－_－) zzZ
