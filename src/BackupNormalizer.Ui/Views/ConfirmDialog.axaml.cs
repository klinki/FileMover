using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BackupNormalizer.Ui.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message)
    {
        InitializeComponent();
        Title = title;
        var text = this.FindControl<TextBlock>("MessageText");
        if (text != null) text.Text = message;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
