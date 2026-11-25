using Avalonia.Controls;
using Avalonia.Platform.Storage;
using REM.ViewModels;

namespace REM.Views;

public partial class WatchPartyView : UserControl
{
    private WatchPartyViewModel? _boundVm;

    public WatchPartyView()
    {
        InitializeComponent();
        DataContextChanged += OnViewDataContextChanged;
    }

    private void OnViewDataContextChanged(object? sender, EventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.PickVideoFileRequested = null;
            _boundVm.ReadClipboardTextRequested = null;
            _boundVm.WriteClipboardTextRequested = null;
            _boundVm = null;
        }

        if (DataContext is WatchPartyViewModel vm)
        {
            _boundVm = vm;
            vm.PickVideoFileRequested = PickVideoFileAsync;
            vm.ReadClipboardTextRequested = ReadClipboardAsync;
            vm.WriteClipboardTextRequested = WriteClipboardAsync;
        }
    }

    private async Task<string?> PickVideoFileAsync()
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null)
        {
            return null;
        }

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose Video File to Sync in VLC",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Video Files")
                {
                    Patterns = ["*.mp4", "*.mkv", "*.webm", "*.avi", "*.mov", "*.m4v", "*.ts"]
                },
                FilePickerFileTypes.All
            ]
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> ReadClipboardAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        return clipboard is null ? null : await clipboard.GetTextAsync();
    }

    private async Task WriteClipboardAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
