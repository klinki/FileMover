using Avalonia.Controls;
using Avalonia.Interactivity;
using BackupNormalizer.Ui.ViewModels;

namespace BackupNormalizer.Ui.Views;

public partial class MkdirDialog : Window
{
    public MkdirDialog()
    {
        InitializeComponent();
        DataContext = new MkdirDialogViewModel();
        Opened += (_, _) =>
        {
            var box = this.FindControl<TextBox>("NameBox");
            box?.Focus();
            box?.SelectAll();
        };
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var name = (DataContext as MkdirDialogViewModel)?.FolderName?.Trim();
        Close(string.IsNullOrEmpty(name) ? null : name);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
