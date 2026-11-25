using Avalonia.Controls;
using Avalonia.Platform.Storage;
using REM.ViewModels;

namespace REM.Views;

public partial class MediaGrabberView : UserControl
{
    private MediaGrabberViewModel? _boundVm;

    public MediaGrabberView()
    {
        InitializeComponent();
        DataContextChanged += OnViewDataContextChanged;
    }

    private void OnViewDataContextChanged(object? sender, EventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.PickFolderRequested = null;
            _boundVm.PickExecutableRequested = null;
            _boundVm.ReadClipboardTextRequested = null;
            _boundVm = null;
        }

        if (DataContext is MediaGrabberViewModel vm)
        {
            _boundVm = vm;
            vm.PickFolderRequested = PickFolderAsync;
            vm.PickExecutableRequested = PickExecutableAsync;
            vm.ReadClipboardTextRequested = ReadClipboardAsync;
        }
    }

    private async Task<string?> PickFolderAsync()
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null)
        {
            return null;
        }

        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose Download Folder",
            AllowMultiple = false
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickExecutableAsync(string title)
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null)
        {
            return null;
        }

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Executables") { Patterns = ["*.exe"] },
                FilePickerFileTypes.All
            ]
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> ReadClipboardAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return null;
        }

        return await clipboard.GetTextAsync();
    }
}
