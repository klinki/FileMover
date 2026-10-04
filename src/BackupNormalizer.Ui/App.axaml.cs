using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BackupNormalizer.Ui.Models;
using BackupNormalizer.Ui.ViewModels;
using BackupNormalizer.Ui.Views;

namespace BackupNormalizer.Ui;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            viewModel.LoadConfiguration();
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
                SessionStore = new GuiSessionStore(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
