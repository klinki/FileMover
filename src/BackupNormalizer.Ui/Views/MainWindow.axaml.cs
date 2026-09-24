using Avalonia.Controls;
using Avalonia.Input;
using BackupNormalizer.Ui.ViewModels;

namespace BackupNormalizer.Ui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnLeftPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm != null) Vm.IsLeftActive = true;
    }

    private void OnRightPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm != null) Vm.IsLeftActive = false;
    }

    private void OnLeftDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm == null) return;
        Vm.IsLeftActive = true;
        Vm.EnterSelected();
    }

    private void OnRightDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm == null) return;
        Vm.IsLeftActive = false;
        Vm.EnterSelected();
    }
}
