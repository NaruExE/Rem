using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using REM.Models;

namespace REM.Controls;

/// <summary>
/// Lightweight TextBlock renderer for Twitch chat lines supporting colored usernames,
/// role badges, inline official Twitch emotes, and inline 7TV emotes.
/// </summary>
public sealed class ChatMessageTextBlock : TextBlock
{
    public static readonly StyledProperty<TwitchChatMessage?> MessageProperty =
        AvaloniaProperty.Register<ChatMessageTextBlock, TwitchChatMessage?>(nameof(Message));

    private static readonly IBrush DefaultUserBrush = new SolidColorBrush(Color.Parse("#9BA8C7"));
    private static readonly IBrush FallbackMutedBrush = new SolidColorBrush(Color.Parse("#8B93A6"));
    private static readonly IBrush FallbackPrimaryBrush = new SolidColorBrush(Color.Parse("#E6E8EE"));
    private static readonly IBrush FallbackBadgeBrush = new SolidColorBrush(Color.Parse("#A970FF"));

    private TwitchChatMessage? _hookedMessage;
    private bool _isAttached;

    static ChatMessageTextBlock()
    {
        MessageProperty.Changed.AddClassHandler<ChatMessageTextBlock>((ctrl, args) =>
            ctrl.OnMessageChanged(args.OldValue as TwitchChatMessage, args.NewValue as TwitchChatMessage));
    }

    public ChatMessageTextBlock()
    {
        TextWrapping = TextWrapping.Wrap;
        FontSize = 12.5;
        LineHeight = 22;
    }

    public TwitchChatMessage? Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        HookMessage(Message);
        RebuildInlines();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        UnhookMessage();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnMessageChanged(TwitchChatMessage? oldMsg, TwitchChatMessage? newMsg)
    {
        if (oldMsg is not null)
        {
            oldMsg.EmotesUpdated -= OnMessageEmotesUpdated;
        }

        if (_isAttached)
        {
            HookMessage(newMsg);
            RebuildInlines();
        }
    }

    private void HookMessage(TwitchChatMessage? msg)
    {
        if (ReferenceEquals(_hookedMessage, msg))
        {
            return;
        }

        UnhookMessage();
        if (msg is not null)
        {
            msg.EmotesUpdated += OnMessageEmotesUpdated;
            _hookedMessage = msg;
        }
    }

    private void UnhookMessage()
    {
        if (_hookedMessage is not null)
        {
            _hookedMessage.EmotesUpdated -= OnMessageEmotesUpdated;
            _hookedMessage = null;
        }
    }

    private void OnMessageEmotesUpdated(object? sender, EventArgs e) => RebuildInlines();

    private void RebuildInlines()
    {
        var inlines = Inlines;
        if (inlines is null)
        {
            return;
        }

        inlines.Clear();
        var msg = Message;
        if (msg is null)
        {
            return;
        }

        var mutedBrush = ResolveBrush("TextMutedBrush", FallbackMutedBrush);
        var primaryBrush = ResolveBrush("TextPrimaryBrush", FallbackPrimaryBrush);

        if (msg.IsSystemNotice)
        {
            var noticeText = msg.Segments.Count > 0 ? msg.Segments[0].Text : string.Empty;
            inlines.Add(new Run(noticeText)
            {
                Foreground = mutedBrush,
                FontStyle = FontStyle.Italic,
                FontSize = 11.5
            });
            return;
        }

        if (!string.IsNullOrWhiteSpace(msg.BadgeTag))
        {
            inlines.Add(new Run($"[{msg.BadgeTag}] ")
            {
                Foreground = FallbackBadgeBrush,
                FontWeight = FontWeight.Bold,
                FontSize = 10.5
            });
        }

        inlines.Add(new Run(msg.UserName)
        {
            Foreground = ParseReadableUserBrush(msg.ColorHex),
            FontWeight = FontWeight.SemiBold
        });

        inlines.Add(new Run(": ")
        {
            Foreground = mutedBrush
        });

        foreach (var seg in msg.Segments)
        {
            if (seg.IsEmote && seg.EmoteBitmap is { } bmp)
            {
                var img = new Image
                {
                    Source = bmp,
                    Height = 20,
                    MaxWidth = 64,
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(1, 0, 1, -4)
                };
                ToolTip.SetTip(img, seg.Text);
                inlines.Add(new InlineUIContainer { Child = img });
            }
            else if (!string.IsNullOrEmpty(seg.Text))
            {
                inlines.Add(new Run(seg.Text)
                {
                    Foreground = primaryBrush
                });
            }
        }
    }

    private IBrush ResolveBrush(string resourceKey, IBrush fallback)
    {
        if (this.TryFindResource(resourceKey, ActualThemeVariant, out var res) && res is IBrush brush)
        {
            return brush;
        }

        return fallback;
    }

    private static IBrush ParseReadableUserBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || !Color.TryParse(hex, out var color))
        {
            return DefaultUserBrush;
        }

        // Lift overly dark Twitch name colors (e.g., #0000FF) so they remain legible on dark chat surfaces.
        var luma = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
        if (luma < 95)
        {
            var r = (byte)Math.Clamp((int)(color.R + (255 - color.R) * 0.45), 0, 255);
            var g = (byte)Math.Clamp((int)(color.G + (255 - color.G) * 0.45), 0, 255);
            var b = (byte)Math.Clamp((int)(color.B + (255 - color.B) * 0.45), 0, 255);
            color = Color.FromRgb(r, g, b);
        }

        return new SolidColorBrush(color);
    }
}
