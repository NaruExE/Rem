using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace REM.Models;

public sealed record SevenTvEmote(string Id, string Name, string ImageUrl);

public sealed record TwitchEmoteRange(string EmoteId, int StartIndex, int EndIndex);

public sealed record RawTwitchChatMessage(
    string Id,
    string UserName,
    string ColorHex,
    string? BadgeTag,
    string Text,
    IReadOnlyList<TwitchEmoteRange> TwitchEmotes);

public sealed partial class TwitchChatSegment : ObservableObject
{
    public TwitchChatSegment(string text, bool isEmote, string? emoteKey, bool isSevenTv, Bitmap? emoteBitmap = null)
    {
        Text = text;
        IsEmote = isEmote;
        EmoteKey = emoteKey;
        IsSevenTv = isSevenTv;
        _emoteBitmap = emoteBitmap;
    }

    public string Text { get; }
    public bool IsEmote { get; }
    public string? EmoteKey { get; }
    public bool IsSevenTv { get; }

    // Owned and disposed exclusively by TwitchEmoteCacheService.
    [ObservableProperty]
    private Bitmap? _emoteBitmap;
}

public sealed class TwitchChatMessage
{
    public TwitchChatMessage(
        string id,
        string userName,
        string colorHex,
        string? badgeTag,
        bool isSystemNotice,
        IReadOnlyList<TwitchChatSegment> segments)
    {
        Id = id;
        UserName = userName;
        ColorHex = colorHex;
        BadgeTag = badgeTag;
        IsSystemNotice = isSystemNotice;
        Segments = segments;
    }

    public string Id { get; }
    public string UserName { get; }
    public string ColorHex { get; }
    public string? BadgeTag { get; }
    public bool IsSystemNotice { get; }
    public IReadOnlyList<TwitchChatSegment> Segments { get; }

    public event EventHandler? EmotesUpdated;

    public void NotifyEmotesUpdated() => EmotesUpdated?.Invoke(this, EventArgs.Empty);
}
