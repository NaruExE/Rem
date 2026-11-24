using Avalonia.Controls;
using Avalonia.Input;
using REM.ViewModels;

namespace REM.Views;

public partial class ProfilesView : UserControl
{
    public ProfilesView()
    {
        InitializeComponent();
    }

    private void OnAddProfileKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ProfilesViewModel vm)
        {
            vm.ConfirmAddProfileCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnUnlockKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ProfilesViewModel vm)
        {
            vm.ConfirmUnlockCommand.Execute(null);
            e.Handled = true;
        }
    }
}

