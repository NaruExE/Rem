using Avalonia.Controls;
using Avalonia.Input;
using REM.ViewModels;

namespace REM.Views;

public partial class WelcomeView : UserControl
{
    public WelcomeView()
    {
        InitializeComponent();
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is WelcomeViewModel vm)
        {
            vm.Skip();
        }
    }
}

