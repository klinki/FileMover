using Avalonia.Controls;
using Avalonia.Input;
using BackupNormalizer.Ui.ViewModels;

namespace BackupNormalizer.Ui.Views;

public partial class StagedOperationsWindow : Window
{
    public StagedOperationsWindow()
    {
        InitializeComponent();
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || e.KeyModifiers != KeyModifiers.None) return;
        if (e.Source is TextBox) return;
        if (DataContext is not MainViewModel vm) return;
        if (StagedList.SelectedItem is not StagedOpItem item) return;
        e.Handled = true;
        string what = string.IsNullOrEmpty(item.Dest) ? item.Source : $"{item.Source} → {item.Dest}";
        var dialog = new ConfirmDialog("Remove operation", $"Remove staged {item.Type} operation?\n{what}");
        if (await dialog.ShowDialog<bool?>(this) == true)
            vm.RemoveStaged(item);
    }
}
