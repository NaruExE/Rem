using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using REM.ViewModels;

namespace REM.Views;

public partial class MediaStudioView : UserControl
{
    private MediaStudioViewModel? _boundVm;

    public MediaStudioView()
    {
        InitializeComponent();
        DataContextChanged += OnViewDataContextChanged;
        AddHandler(DragDrop.DropEvent, OnFilesDropped);
    }

    private void OnViewDataContextChanged(object? sender, EventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.PickFilesRequested = null;
            _boundVm.PickFolderRequested = null;
            _boundVm = null;
        }

        if (DataContext is MediaStudioViewModel vm)
        {
            _boundVm = vm;
            vm.PickFilesRequested = PickFilesAsync;
            vm.PickFolderRequested = PickFolderAsync;
        }
    }

    private void OnFilesDropped(object? sender, DragEventArgs e)
    {
        if (_boundVm is null)
        {
            return;
        }

        var items = e.Data.GetFiles();
        if (items is null)
        {
            return;
        }

        var paths = items
            .Select(i => i.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Cast<string>()
            .ToList();

        if (paths.Count > 0)
        {
            _boundVm.SetInputFiles(paths);
        }
    }

    private async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null)
        {
            return [];
        }

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose Video, Audio, or Image Files",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Media Files")
                {
                    Patterns =
                    [
                        "*.mp4", "*.mkv", "*.webm", "*.mov", "*.avi", "*.flv", "*.ts",
                        "*.mp3", "*.m4a", "*.wav", "*.flac", "*.opus", "*.ogg",
                        "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"
                    ]
                },
                FilePickerFileTypes.All
            ]
        });

        return files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Cast<string>()
            .ToList();
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
            Title = "Choose Output Folder",
            AllowMultiple = false
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
