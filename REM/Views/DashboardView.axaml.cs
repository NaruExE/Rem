using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using REM.ViewModels;

namespace REM.Views;

public partial class DashboardView : UserControl
{
    private const double DockEdgeMargin = 18;
    private readonly TranslateTransform _miniPlayerTransform = new();
    private bool _isDraggingMiniPlayer;
    private Point _dragStartPointerPos;
    private Point _dragStartOffset;
    private DashboardViewModel? _boundVm;

    public DashboardView()
    {
        InitializeComponent();
        MiniPlayerDock.RenderTransform = _miniPlayerTransform;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        DataContextChanged += OnDataContextChangedHandler;
        WorkspaceHostGrid.SizeChanged += OnLayoutSizeChanged;
        MiniPlayerDock.SizeChanged += OnLayoutSizeChanged;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e) =>
        RestoreAndClampMiniPlayerPosition();

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _isDraggingMiniPlayer = false;
        _boundVm = null;
    }

    private void OnDataContextChangedHandler(object? sender, EventArgs e)
    {
        _boundVm = DataContext as DashboardViewModel;
        RestoreAndClampMiniPlayerPosition();
    }

    private void OnLayoutSizeChanged(object? sender, SizeChangedEventArgs e) =>
        ClampAndApplyMiniPlayerOffset(_miniPlayerTransform.X, _miniPlayerTransform.Y);

    private void RestoreAndClampMiniPlayerPosition()
    {
        _boundVm ??= DataContext as DashboardViewModel;
        if (_boundVm is null)
        {
            return;
        }

        ClampAndApplyMiniPlayerOffset(_boundVm.MiniPlayer.SavedOffsetX, _boundVm.MiniPlayer.SavedOffsetY);
    }

    private void ClampAndApplyMiniPlayerOffset(double desiredX, double desiredY)
    {
        var hostW = WorkspaceHostGrid.Bounds.Width;
        var hostH = WorkspaceHostGrid.Bounds.Height;
        var dockW = MiniPlayerDock.Bounds.Width;
        var dockH = MiniPlayerDock.Bounds.Height;

        if (hostW <= 0 || hostH <= 0 || dockW <= 0 || dockH <= 0)
        {
            _miniPlayerTransform.X = desiredX;
            _miniPlayerTransform.Y = desiredY;
            return;
        }

        // Dock is bottom-right aligned with (right=18, bottom=18) margin inside WorkspaceHostGrid.
        var minX = Math.Min(0, -(hostW - dockW - DockEdgeMargin * 2));
        var minY = Math.Min(0, -(hostH - dockH - DockEdgeMargin * 2));
        _miniPlayerTransform.X = Math.Clamp(desiredX, minX, 0);
        _miniPlayerTransform.Y = Math.Clamp(desiredY, minY, 0);
    }

    private void OnMiniPlayerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(WorkspaceHostGrid);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var isHandle = ReferenceEquals(sender, MiniPlayerDragHandle);
        if (!isHandle && e.Source is Visual src)
        {
            // Never hijack clicks destined for interactive buttons inside the card.
            var cur = src;
            while (cur is not null && !ReferenceEquals(cur, MiniPlayerCard))
            {
                if (cur is Button)
                {
                    return;
                }

                cur = cur.GetVisualParent();
            }
        }

        if (isHandle && e.ClickCount >= 2)
        {
            _isDraggingMiniPlayer = false;
            _miniPlayerTransform.X = 0;
            _miniPlayerTransform.Y = 0;
            (_boundVm ?? DataContext as DashboardViewModel)?.MiniPlayer.SaveOffset(0, 0);
            e.Handled = true;
            return;
        }

        _isDraggingMiniPlayer = true;
        _dragStartPointerPos = point.Position;
        _dragStartOffset = new Point(_miniPlayerTransform.X, _miniPlayerTransform.Y);
        e.Pointer.Capture(MiniPlayerCard);
        e.Handled = true;
    }

    private void OnMiniPlayerPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingMiniPlayer)
        {
            return;
        }

        var pos = e.GetCurrentPoint(WorkspaceHostGrid).Position;
        var delta = pos - _dragStartPointerPos;
        ClampAndApplyMiniPlayerOffset(_dragStartOffset.X + delta.X, _dragStartOffset.Y + delta.Y);
    }

    private void OnMiniPlayerPointerReleased(object? sender, PointerReleasedEventArgs e) =>
        EndMiniPlayerDrag(e.Pointer);

    private void OnMiniPlayerPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_isDraggingMiniPlayer)
        {
            _isDraggingMiniPlayer = false;
            (_boundVm ?? DataContext as DashboardViewModel)?.MiniPlayer.SaveOffset(_miniPlayerTransform.X, _miniPlayerTransform.Y);
        }
    }

    private void EndMiniPlayerDrag(IPointer pointer)
    {
        if (!_isDraggingMiniPlayer)
        {
            return;
        }

        _isDraggingMiniPlayer = false;
        if (ReferenceEquals(pointer.Captured, MiniPlayerCard))
        {
            pointer.Capture(null);
        }

        (_boundVm ?? DataContext as DashboardViewModel)?.MiniPlayer.SaveOffset(_miniPlayerTransform.X, _miniPlayerTransform.Y);
    }
}

