using Avalonia;
using Avalonia.Controls;
using REM.ViewModels;

namespace REM.Views;

public partial class AudioStreamerView : UserControl
{
    private AudioStreamerViewModel? _boundVm;

    public AudioStreamerView()
    {
        InitializeComponent();
        DataContextChanged += OnViewDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnViewDataContextChanged(object? sender, EventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.WriteClipboardTextRequested = null;
            _boundVm = null;
        }

        if (DataContext is AudioStreamerViewModel vm)
        {
            _boundVm = vm;
            vm.WriteClipboardTextRequested = WriteClipboardAsync;
        }
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is AudioStreamerViewModel vm)
        {
            _boundVm = vm;
            vm.WriteClipboardTextRequested = WriteClipboardAsync;
        }
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.WriteClipboardTextRequested = null;
        }
    }

    private async Task WriteClipboardAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null && !string.IsNullOrWhiteSpace(text))
        {
            await clipboard.SetTextAsync(text);
        }
    }
}

