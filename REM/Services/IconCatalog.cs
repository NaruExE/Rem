using Avalonia.Media;

namespace REM.Services;

public static class IconCatalog
{
    // Spotify: recognizable 3 curved sound-wave arcs (filled geometry)
    public static Geometry Spotify { get; } = StreamGeometry.Parse(
        "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20zm4.58 14.42a.62.62 0 0 1-.86.21c-2.35-1.44-5.31-1.76-8.8-.96a.62.62 0 1 1-.28-1.22c3.81-.87 7.09-.5 9.73 1.11.3.18.39.57.21.86zm1.22-2.72a.78.78 0 0 1-1.07.26c-2.69-1.65-6.79-2.13-9.97-1.17a.78.78 0 1 1-.45-1.49c3.63-1.1 8.15-.57 11.23 1.33.37.22.48.7.26 1.07zm.11-2.83C14.68 8.95 9.36 8.77 6.28 9.71a.94.94 0 1 1-.55-1.79c3.54-1.08 9.42-.87 13.13 1.33a.94.94 0 0 1-.95 1.62z");

    // Audio Streamer (YT/Twitch): broadcast play shield/badge (filled geometry)
    public static Geometry AudioStreamer { get; } = StreamGeometry.Parse(
        "M4 4h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2h-4l-4 3-4-3H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2zm6 4.2v5.6l5-2.8-5-2.8z");

    // Calendar: crisp header + date grid (filled geometry)
    public static Geometry Calendar { get; } = StreamGeometry.Parse(
        "M7 2a1 1 0 0 1 1 1v1h8V3a1 1 0 1 1 2 0v1h1a2 2 0 0 1 2 2v13a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h1V3a1 1 0 0 1 1-1zm12 8H5v9h14v-9zM7 12h3v3H7v-3zm5 0h5v2h-5v-2z");

    // To-Do List: rounded task card with check & lines (filled geometry)
    public static Geometry TodoList { get; } = StreamGeometry.Parse(
        "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2zm11.7 5.3a1 1 0 0 0-1.4-1.4L10 12.2 8.2 10.4a1 1 0 1 0-1.4 1.4l2.5 2.5a1 1 0 0 0 1.4 0l6-6zM7 17h10v-1.5H7V17z");

    public static Geometry Home { get; } = StreamGeometry.Parse(
        "M12 3L3 10.5V21h6v-6h6v6h6V10.5L12 3z");

    public static Geometry Settings { get; } = StreamGeometry.Parse(
        "M19.14 12.94a7.9 7.9 0 0 0 .06-.94c0-.32-.02-.63-.06-.94l2.03-1.58a.5.5 0 0 0 .12-.64l-1.92-3.32a.5.5 0 0 0-.61-.22l-2.39.96a7.3 7.3 0 0 0-1.63-.94l-.36-2.54a.49.49 0 0 0-.49-.42h-3.84a.49.49 0 0 0-.49.42l-.36 2.54c-.58.24-1.13.56-1.63.94l-2.39-.96a.49.49 0 0 0-.61.22L2.7 8.84a.49.49 0 0 0 .12.64l2.03 1.58c-.04.31-.06.62-.06.94s.02.63.06.94l-2.03 1.58a.5.5 0 0 0-.12.64l1.92 3.32a.5.5 0 0 0 .61.22l2.39-.96c.5.38 1.05.7 1.63.94l.36 2.54c.04.24.25.42.49.42h3.84c.24 0 .45-.18.49-.42l.36-2.54c.58-.24 1.13-.56 1.63-.94l2.39.96a.49.49 0 0 0 .61-.22l1.92-3.32a.5.5 0 0 0-.12-.64l-2.03-1.58zM12 15.5a3.5 3.5 0 1 1 0-7 3.5 3.5 0 0 1 0 7z");

    public static Geometry Lock { get; } = StreamGeometry.Parse(
        "M7 10V7a5 5 0 0 1 10 0v3h1a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2v-8a2 2 0 0 1 2-2h1zm2 0h6V7a3 3 0 0 0-6 0v3z");

    public static Geometry Plus { get; } = StreamGeometry.Parse(
        "M11 4h2v7h7v2h-7v7h-2v-7H4v-2h7V4z");

    public static Geometry Close { get; } = StreamGeometry.Parse(
        "M6.7 5.3L12 10.6l5.3-5.3 1.4 1.4L13.4 12l5.3 5.3-1.4 1.4L12 13.4l-5.3 5.3-1.4-1.4L10.6 12 5.3 6.7 6.7 5.3z");

    public static Geometry Image { get; } = StreamGeometry.Parse(
        "M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2zm0 14h16v-2.5l-4.5-4.5-4 4.5-2.5-2.5L4 18zm4.5-8.5a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3z");

    public static Geometry Trash { get; } = StreamGeometry.Parse(
        "M9 3h6l1 2h4v2H4V5h4l1-2zm-2 6h10l-1 11H8L7 9z");

    public static Geometry SwitchProfile { get; } = StreamGeometry.Parse(
        "M16 3h5v5h-2V6.4l-5.3 5.3-1.4-1.4L17.6 5H16V3zM3 16h2v1.6l5.3-5.3 1.4 1.4L6.4 19H8v2H3v-5z");

    public static Geometry Play { get; } = StreamGeometry.Parse(
        "M7 5v14l12-7L7 5z");

    public static Geometry Pause { get; } = StreamGeometry.Parse(
        "M6 5h4v14H6V5zm8 0h4v14h-4V5z");

    // Eye-off / Headless filled badge
    public static Geometry Headless { get; } = StreamGeometry.Parse(
        "M3.3 2L2 3.3l3.1 3.1C3.2 7.8 1.8 9.8 1 12c1.7 4.4 6 7.5 11 7.5 1.9 0 3.7-.5 5.3-1.3l3.4 3.4 1.3-1.3L3.3 2zM12 16.5c-2.5 0-4.5-2-4.5-4.5 0-.6.1-1.2.4-1.8l1.6 1.6v.2c0 1.4 1.1 2.5 2.5 2.5h.2l1.6 1.6c-.6.3-1.2.4-1.8.4zm0-9c2.5 0 4.5 2 4.5 4.5 0 .4-.1.8-.2 1.2l3.1 3.1c1.6-1.1 2.9-2.6 3.6-4.3-1.7-4.4-6-7.5-11-7.5-1.2 0-2.4.2-3.5.6l1.7 1.7c.6-.2 1.2-.3 1.8-.3z");

    // Power / Kill icon (filled)
    public static Geometry Kill { get; } = StreamGeometry.Parse(
        "M11 2h2v10h-2V2zm5.6 2.6l-1.4 1.4A6.98 6.98 0 0 1 19 12a7 7 0 1 1-14 0c0-2.5 1.3-4.7 3.8-6L7.4 4.6A8.98 8.98 0 0 0 3 12a9 9 0 1 0 18 0 8.98 8.98 0 0 0-4.4-7.4z");

    public static Geometry Check { get; } = StreamGeometry.Parse(
        "M9 16.2L4.8 12l-1.4 1.4L9 19 21 7l-1.4-1.4L9 16.2z");

    public static Geometry SidebarCollapse { get; } = StreamGeometry.Parse(
        "M3 4h18v16H3V4zm2 2v12h5V6H5zm7 0v12h7V6h-7zm3.4 3.3L12.7 12l2.7 2.7-1.4 1.4L10 12l4-4.1 1.4 1.4z");

    public static Geometry SidebarExpand { get; } = StreamGeometry.Parse(
        "M3 4h18v16H3V4zm2 2v12h5V6H5zm7 0v12h7V6h-7zm1.3 3.3L16 12l-2.7 2.7 1.4 1.4 4-4.1-4-4.1-1.4 1.4z");

    public static Geometry ChevronLeft { get; } = StreamGeometry.Parse(
        "M15.4 7.4L14 6l-6 6 6 6 1.4-1.4L10.8 12l4.6-4.6z");

    public static Geometry ChevronRight { get; } = StreamGeometry.Parse(
        "M10 6L8.6 7.4 13.2 12l-4.6 4.6L10 18l6-6-6-6z");

    public static Geometry User { get; } = StreamGeometry.Parse(
        "M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8zm0 2c-4.4 0-8 2.2-8 5v2h16v-2c0-2.8-3.6-5-8-5z");

    public static Geometry Palette { get; } = StreamGeometry.Parse(
        "M12 2a10 10 0 0 0 0 20c1.1 0 2-.9 2-2 0-.5-.2-1-.5-1.3-.3-.4-.5-.8-.5-1.3 0-1.1.9-2 2-2h2.3c2.6 0 4.7-2.1 4.7-4.7C22 6 17.5 2 12 2zm-5.5 9a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3zm3-4a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3zm5 0a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3zm3 4a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3z");

    public static Geometry Folder { get; } = StreamGeometry.Parse(
        "M3 5h6l2 2h10a1 1 0 0 1 1 1v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a1 1 0 0 1 1-1z");

    public static Geometry Key { get; } = StreamGeometry.Parse(
        "M7 14a5 5 0 1 1 4.6-7H22v4h-2v3h-3v-3h-5.4A5 5 0 0 1 7 14zm0-3a2 2 0 1 0 0-4 2 2 0 0 0 0 4z");

    public static Geometry AlertTriangle { get; } = StreamGeometry.Parse(
        "M12 3L2 20h20L12 3zm-1 6h2v5h-2V9zm0 7h2v2h-2v-2z");

    public static Geometry AlertCircle { get; } = StreamGeometry.Parse(
        "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20zm-1 5h2v7h-2V7zm0 9h2v2h-2v-2z");

    public static Geometry InfoCircle { get; } = StreamGeometry.Parse(
        "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20zm-1 5h2v2h-2V7zm0 4h2v6h-2v-6z");

    public static Geometry FileText { get; } = StreamGeometry.Parse(
        "M6 2h8l5 5v13a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2zm7 1.5V8h4.5L13 3.5zM8 12h8v1.5H8V12zm0 3.5h8V17H8v-1.5zm0-7h3.5V10H8V8.5z");

    public static Geometry WindowMinimize { get; } = StreamGeometry.Parse(
        "M5 11h14v2H5z");

    public static Geometry WindowMaximize { get; } = StreamGeometry.Parse(
        "M4 4h16v16H4V4zm2 2v12h12V6H6z");

    public static Geometry WindowRestore { get; } = StreamGeometry.Parse(
        "M8 4h12v12h-2V6H8V4zM4 8h12v12H4V8zm2 2v8h8v-8H6z");

    public static Geometry ChevronUp { get; } = StreamGeometry.Parse(
        "M7.4 15.4L6 14l6-6 6 6-1.4 1.4L12 10.8l-4.6 4.6z");

    public static Geometry ChevronDown { get; } = StreamGeometry.Parse(
        "M7.4 8.6L12 13.2l4.6-4.6L18 10l-6 6-6-6 1.4-1.4z");

    public static Geometry Refresh { get; } = StreamGeometry.Parse(
        "M17.65 6.35A7.95 7.95 0 0 0 12 4a8 8 0 1 0 7.75 10h-2.08A6 6 0 1 1 12 6c1.66 0 3.14.69 4.22 1.78L13 11h7V4l-2.35 2.35z");

    public static Geometry ExternalLink { get; } = StreamGeometry.Parse(
        "M14 3h7v7h-2V6.4l-8.3 8.3-1.4-1.4L17.6 5H14V3zM5 5h6v2H7v10h10v-4h2v6H5V5z");

    public static Geometry YouTube { get; } = StreamGeometry.Parse(
        "M21.58 7.19a2.75 2.75 0 0 0-1.94-1.94C17.93 4.8 12 4.8 12 4.8s-5.93 0-7.64.45A2.75 2.75 0 0 0 2.42 7.2 28.6 28.6 0 0 0 2 12a28.6 28.6 0 0 0 .42 4.81 2.75 2.75 0 0 0 1.94 1.94C6.07 19.2 12 19.2 12 19.2s5.93 0 7.64-.45a2.75 2.75 0 0 0 1.94-1.94A28.6 28.6 0 0 0 22 12a28.6 28.6 0 0 0-.42-4.81zM10 15V9l5.2 3L10 15z");

    public static Geometry Twitch { get; } = StreamGeometry.Parse(
        "M4.2 3L3 6.2V19h4.5v2h2.4l2-2h3.1l4.8-4.8V3H4.2zm14.1 10.4L15.6 16H11l-2.2 2.2V16H5.5V4.8h12.8v8.6zM14.5 7.5h1.6v4.6h-1.6V7.5zm-4.4 0h1.6v4.6h-1.6V7.5z");

    public static Geometry Heart { get; } = StreamGeometry.Parse(
        "M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z");

    public static Geometry HeartOutline { get; } = StreamGeometry.Parse(
        "M16.5 3c-1.74 0-3.41.81-4.5 2.09C10.91 3.81 9.24 3 7.5 3 4.42 3 2 5.42 2 8.5c0 3.78 3.4 6.86 8.55 11.54L12 21.35l1.45-1.32C18.6 15.36 22 12.28 22 8.5 22 5.42 19.58 3 16.5 3zm-4.4 15.55l-.1.1-.1-.1C7.14 14.24 4 11.39 4 8.5 4 6.5 5.5 5 7.5 5c1.54 0 3.04.99 3.57 2.36h1.87C13.46 5.99 14.96 5 16.5 5c2 0 3.5 1.5 3.5 3.5 0 2.89-3.14 5.74-7.9 10.05z");

    public static Geometry SkipNext { get; } = StreamGeometry.Parse(
        "M6 18l8.5-6L6 6v12zM16 6v12h2V6h-2z");

    public static Geometry SkipPrev { get; } = StreamGeometry.Parse(
        "M6 6h2v12H6zm3.5 6l8.5 6V6z");

    public static Geometry Shuffle { get; } = StreamGeometry.Parse(
        "M10.59 9.17L5.41 4 4 5.41l5.17 5.17 1.42-1.41zM14.5 4l2.04 2.04L4 18.59 5.41 20 17.96 7.46 20 9.5V4h-5.5zm.33 9.41l-1.41 1.41 3.13 3.13L14.5 20H20v-5.5l-2.04 2.04-3.13-3.13z");

    public static Geometry Repeat { get; } = StreamGeometry.Parse(
        "M7 7h10v3l4-4-4-4v3H5v6h2V7zm10 10H7v-3l-4 4 4 4v-3h12v-6h-2v4z");

    public static Geometry QueueList { get; } = StreamGeometry.Parse(
        "M3 10h11v2H3v-2zm0-4h11v2H3V6zm0 8h7v2H3v-2zm13-1v8l6-4-6-4z");

    public static Geometry Lyrics { get; } = StreamGeometry.Parse(
        "M12 3v10.55A4 4 0 1 0 14 17V7h4V3h-6zM4 7h6v2H4V7zm0 4h6v2H4v-2zm0 4h4v2H4v-2z");

    public static Geometry Search { get; } = StreamGeometry.Parse(
        "M15.5 14h-.79l-.28-.27A6.47 6.47 0 0 0 16 9.5 6.5 6.5 0 1 0 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z");

    public static Geometry VolumeHigh { get; } = StreamGeometry.Parse(
        "M3 9v6h4l5 5V4L7 9H3zm13.5 3A4.5 4.5 0 0 0 14 7.97v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z");

    public static Geometry Sparkles { get; } = StreamGeometry.Parse(
        "M12 2l2.2 5.8L20 10l-5.8 2.2L12 18l-2.2-5.8L4 10l5.8-2.2L12 2zm7 12l1.1 2.9L23 18l-2.9 1.1L19 22l-1.1-2.9L15 18l2.9-1.1L19 14z");

    public static Geometry MusicNote { get; } = StreamGeometry.Parse(
        "M12 3v10.55c-.59-.34-1.27-.55-2-.55-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4V7h4V3h-6z");

    public static Geometry Copy { get; } = StreamGeometry.Parse(
        "M16 1H4a2 2 0 0 0-2 2v14h2V3h12V1zm3 4H8a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h11a2 2 0 0 0 2-2V7a2 2 0 0 0-2-2zm0 16H8V7h11v14z");

    public static Geometry Pin { get; } = StreamGeometry.Parse(
        "M16 12V4h1V2H7v2h1v8l-2 2v2h5.2v6h1.6v-6H18v-2l-2-2z");

    public static Geometry Fullscreen { get; } = StreamGeometry.Parse(
        "M7 14H5v5h5v-2H7v-3zm-2-4h2V7h3V5H5v5zm12 7h-3v2h5v-5h-2v3zM14 5v2h3v3h2V5h-5z");

    public static Geometry ExitFullscreen { get; } = StreamGeometry.Parse(
        "M5 16h3v3h2v-5H5v2zm3-8H5v2h5V5H8v3zm6 11h2v-3h3v-2h-5v5zm2-11V5h-2v5h5V8h-3z");

    public static Geometry Share { get; } = StreamGeometry.Parse(
        "M18 16.08c-.76 0-1.44.3-1.96.77L8.91 12.7c.05-.23.09-.46.09-.7s-.04-.47-.09-.7l7.05-4.11c.54.5 1.25.81 2.04.81 1.66 0 3-1.34 3-3s-1.34-3-3-3-3 1.34-3 3c0 .24.04.47.09.7L8.04 9.81C7.5 9.31 6.79 9 6 9c-1.66 0-3 1.34-3 3s1.34 3 3 3c.79 0 1.5-.31 2.04-.81l7.12 4.16c-.05.21-.08.43-.08.65 0 1.61 1.31 2.92 2.92 2.92s2.92-1.31 2.92-2.92-1.31-2.92-2.92-2.92z");

    public static Geometry VideoPlayer { get; } = StreamGeometry.Parse(
        "M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2zm2 2v2h2V6H6zm10 0v2h2V6h-2zM6 16v2h2v-2H6zm10 0v2h2v-2h-2zm-6-7.2v6.4L15.5 12 10 8.8z");

    public static Geometry VolumeMute { get; } = StreamGeometry.Parse(
        "M16.5 12A4.5 4.5 0 0 0 14 7.97v2.21l2.45 2.45c.03-.2.05-.41.05-.63zm2.5 0c0 .94-.2 1.82-.54 2.64l1.51 1.51A8.8 8.8 0 0 0 21 12c0-4.28-2.99-7.86-7-8.77v2.06c2.89.86 5 3.54 5 6.71zM4.27 3L3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.06a8.99 8.99 0 0 0 3.69-1.81L19.73 21 21 19.73l-9-9L4.27 3zM12 4L9.91 6.09 12 8.18V4z");

    public static Geometry Subtitles { get; } = StreamGeometry.Parse(
        "M20 4H4a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V6a2 2 0 0 0-2-2zM4 12h4v2H4v-2zm10 6H4v-2h10v2zm6 0h-4v-2h4v2zm0-4H10v-2h10v2z");

    public static Geometry Link { get; } = StreamGeometry.Parse(
        "M3.9 12c0-1.71 1.39-3.1 3.1-3.1h4V7H7a5 5 0 0 0 0 10h4v-1.9H7c-1.71 0-3.1-1.39-3.1-3.1zM8 13h8v-2H8v2zm9-6h-4v1.9h4c1.71 0 3.1 1.39 3.1 3.1s-1.39 3.1-3.1 3.1h-4V17h4a5 5 0 0 0 0-10z");

    public static Geometry MoviesTv { get; } = StreamGeometry.Parse(
        "M18 4l2 4h-3l-2-4h-2l2 4h-3l-2-4H8l2 4H7L5 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V4h-4z");

    public static Geometry Bookmark { get; } = StreamGeometry.Parse(
        "M17 3H7c-1.1 0-2 .9-2 2v16l7-3 7 3V5c0-1.1-.9-2-2-2z");

    public static Geometry BookmarkOutline { get; } = StreamGeometry.Parse(
        "M17 3H7c-1.1 0-2 .9-2 2v16l7-3 7 3V5c0-1.1-.9-2-2-2zm0 15l-5-2.18L7 18V5h10v13z");

    public static Geometry Star { get; } = StreamGeometry.Parse(
        "M12 17.27L18.18 21l-1.64-7.03L22 9.24l-7.19-.61L12 2 9.19 8.63 2 9.24l5.46 4.73L5.82 21z");

    public static Geometry StreamWatch { get; } = StreamGeometry.Parse(
        "M21 3H3a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h5v2h8v-2h5a2 2 0 0 0 2-2V5a2 2 0 0 0-2-2zm0 14H3V5h18v12zm-11-9.5v7l6-3.5-6-3.5z");

    public static Geometry Chat { get; } = StreamGeometry.Parse(
        "M4 3h16a2 2 0 0 1 2 2v11a2 2 0 0 1-2 2H8l-4 3.5V18H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2zm2 5v2h12V8H6zm0 4v2h8v-2H6z");
}
