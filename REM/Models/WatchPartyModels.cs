namespace REM.Models;

public enum WatchPartyRole
{
    None,
    Host,
    Client
}

public enum WatchPartyConnectionMode
{
    CloudflareTunnel,
    DirectLan
}

public sealed record WatchPartyPacket(
    string Type,
    string? Sender = null,
    string? Pin = null,
    string? Action = null,
    long PositionMs = 0,
    bool IsPaused = false,
    float Speed = 1.0f,
    string? MediaUrl = null,
    string? MediaTitle = null,
    string? Text = null,
    List<string>? Peers = null,
    long SentUnixMs = 0);

public sealed record WatchPartyChatMessage(
    string Sender,
    string Text,
    string Timestamp,
    bool IsSystem);
