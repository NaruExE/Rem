using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using REM.Models;

namespace REM.Services;

public sealed partial class DialogButtonViewModel : ObservableObject
{
    private readonly Action<string> _onSelect;

    public DialogButtonViewModel(DialogButtonOption option, Action<string> onSelect)
    {
        Key = option.Key;
        Label = option.Label;
        IsPrimary = option.IsPrimary;
        IsDanger = option.IsDanger;
        IsDefaultStyle = !option.IsPrimary && !option.IsDanger;
        _onSelect = onSelect;
    }

    public string Key { get; }
    public string Label { get; }
    public bool IsPrimary { get; }
    public bool IsDanger { get; }
    public bool IsDefaultStyle { get; }

    [RelayCommand]
    private void Select() => _onSelect(Key);
}

public sealed partial class DialogViewModel : ObservableObject
{
    private readonly Action<string?> _onResolve;

    public DialogViewModel(
        DialogRequest request,
        Bitmap? remArtBitmap,
        Action<string?> onResolve)
    {
        Kind = request.Kind;
        Title = request.Title;
        Message = request.Message;
        Details = request.Details ?? string.Empty;
        RemArtBitmap = remArtBitmap;
        _onResolve = onResolve;

        IconGeometry = request.Kind switch
        {
            DialogKind.Error => IconCatalog.AlertCircle,
            DialogKind.Warning => IconCatalog.AlertTriangle,
            DialogKind.Confirm => IconCatalog.AlertTriangle,
            _ => IconCatalog.InfoCircle
        };

        foreach (var option in request.Buttons)
        {
            Buttons.Add(new DialogButtonViewModel(option, key => _onResolve(key)));
        }
    }

    public DialogKind Kind { get; }
    public string Title { get; }
    public string Message { get; }
    public string Details { get; }
    public bool HasDetails => !string.IsNullOrWhiteSpace(Details);
    public Geometry IconGeometry { get; }
    public Bitmap? RemArtBitmap { get; }
    public bool HasRemArt => RemArtBitmap is not null;

    public bool IsError => Kind == DialogKind.Error;
    public bool IsWarning => Kind == DialogKind.Warning;
    public bool IsConfirm => Kind == DialogKind.Confirm;
    public bool IsInfo => Kind == DialogKind.Info;
    public bool ShowTopRightErrorRem => IsError && HasRemArt;
    public bool ShowRightInfoRem => IsInfo && HasRemArt;
    public bool ShowLeftFooterRem => (IsWarning || IsConfirm) && HasRemArt;

    public ObservableCollection<DialogButtonViewModel> Buttons { get; } = [];

    [RelayCommand]
    private void Dismiss() => _onResolve(null);
}

public sealed partial class DialogService : ObservableObject
{
    private readonly ImageAssetService _imageAssets;
    private readonly ActionLogService _log;
    private TaskCompletionSource<string?>? _pendingTcs;

    public DialogService(ImageAssetService imageAssets, ActionLogService log)
    {
        _imageAssets = imageAssets;
        _log = log;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDialogOpen))]
    private DialogViewModel? _activeDialog;

    public bool IsDialogOpen => ActiveDialog is not null;

    public Task<string?> ShowAsync(DialogRequest request)
    {
        var category = request.Kind.ToString();
        _log.Log(category, $"{request.Title} — {request.Message}");

        if (Dispatcher.UIThread.CheckAccess())
        {
            return ShowOnUiThreadAsync(request);
        }

        return Dispatcher.UIThread.InvokeAsync(() => ShowOnUiThreadAsync(request));
    }

    public Task<string?> ShowFileNotFoundAsync(
        string assetLabel,
        string missingPath,
        bool allowPickNew = true,
        bool allowReset = true)
    {
        var buttons = new List<DialogButtonOption>();
        if (allowPickNew)
        {
            buttons.Add(new DialogButtonOption("pick", "Choose New", IsPrimary: true));
        }

        if (allowReset)
        {
            buttons.Add(new DialogButtonOption("reset", "Reset to Default"));
        }

        buttons.Add(new DialogButtonOption("dismiss", "Dismiss"));

        return ShowAsync(new DialogRequest(
            DialogKind.Warning,
            $"{assetLabel} Not Found",
            $"The selected {assetLabel.ToLowerInvariant()} file could not be found on disk.",
            missingPath,
            buttons));
    }

    public Task ShowErrorAsync(string title, string message, string? details = null) =>
        ShowAsync(new DialogRequest(
            DialogKind.Error,
            title,
            message,
            details,
            [new DialogButtonOption("ok", "OK", IsPrimary: true)]));

    public Task ShowWarningAsync(string title, string message, string? details = null) =>
        ShowAsync(new DialogRequest(
            DialogKind.Warning,
            title,
            message,
            details,
            [new DialogButtonOption("ok", "OK", IsPrimary: true)]));

    public async Task<bool> ShowConfirmAsync(
        string title,
        string message,
        string confirmLabel = "Confirm",
        bool isDanger = true)
    {
        var result = await ShowAsync(new DialogRequest(
            DialogKind.Confirm,
            title,
            message,
            null,
            [
                new DialogButtonOption("cancel", "Cancel"),
                new DialogButtonOption("confirm", confirmLabel, IsPrimary: !isDanger, IsDanger: isDanger)
            ]));

        return string.Equals(result, "confirm", StringComparison.Ordinal);
    }

    public bool TryDismissActive()
    {
        if (ActiveDialog is null)
        {
            return false;
        }

        ResolveActive(null);
        return true;
    }

    private Task<string?> ShowOnUiThreadAsync(DialogRequest request)
    {
        if (_pendingTcs is not null)
        {
            _pendingTcs.TrySetResult(null);
            _pendingTcs = null;
        }

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingTcs = tcs;

        var suffix = request.Kind switch
        {
            DialogKind.Error => "error",
            DialogKind.Warning => "warning",
            DialogKind.Confirm => "confirm",
            _ => "info"
        };

        var art = _imageAssets.GetPopupImage(suffix);
        ActiveDialog = new DialogViewModel(request, art, ResolveActive);
        return tcs.Task;
    }

    private void ResolveActive(string? result)
    {
        var tcs = _pendingTcs;
        _pendingTcs = null;
        ActiveDialog = null;
        tcs?.TrySetResult(result);
    }
}
