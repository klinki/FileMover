using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BackupNormalizer.Ui.ViewModels;

namespace BackupNormalizer.Ui.Views;

public partial class PlanExecutionWindow : Window
{
    private bool _waitingForShutdown;
    private bool _shutdownAllowed;
    private PlanExecutionViewModel? Vm => DataContext as PlanExecutionViewModel;

    public PlanExecutionWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    public PlanExecutionWindow(PlanExecutionViewModel viewModel)
        : this() => DataContext = viewModel;

    private async void OnExecute(object? sender, RoutedEventArgs args)
    {
        var vm = Vm;
        if (vm?.CanRun != true)
        {
            return;
        }

        var confirmation = new ConfirmDialog(
            vm.HasJournal ? "Resume execution" : "Execute reviewed plan",
            $"Apply plan {vm.Document.PlanId}?\n\nSource: {vm.SourcePath}\nTarget: {vm.TargetPath}\n\n{vm.Document.Operations.Count:N0} operations may copy, move, or trash files. Completed operations will be recorded in:\n{vm.DatabasePath}"
        );
        if (await confirmation.ShowDialog<bool>(this))
        {
            await vm.RunAsync();
        }
    }

    private async void OnVerify(object? sender, RoutedEventArgs args)
    {
        if (Vm?.CanVerify == true)
        {
            await Vm.RunAsync(verifyOnly: true);
        }
    }

    private async void OnBrowseRoot(object? sender, RoutedEventArgs args)
    {
        if (Vm == null || sender is not Button button)
        {
            return;
        }

        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = "Select local " + button.Tag + " root",
                    AllowMultiple = false,
                }
            );
            string? path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            if (path == null)
            {
                return;
            }

            if (button.Tag as string == "Source")
            {
                Vm.SourcePath = path;
            }
            else
            {
                Vm.TargetPath = path;
            }
        }
        catch (Exception ex)
        {
            Vm.Status = "Cannot select folder: " + ex.Message;
        }
    }

    private async void OnChooseJournal(object? sender, RoutedEventArgs args)
    {
        if (Vm == null)
        {
            return;
        }

        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Choose a new execution database",
                    SuggestedFileName = "execution.db",
                    DefaultExtension = ".db",
                    ShowOverwritePrompt = true,
                }
            );
            string? path = file?.TryGetLocalPath();
            if (path != null)
            {
                Vm.DatabasePath = path;
            }
        }
        catch (Exception ex)
        {
            Vm.Status = "Cannot select execution database: " + ex.Message;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs args) => Close();

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_shutdownAllowed || Vm?.IsRunning != true)
        {
            return;
        }

        args.Cancel = true;
        if (_waitingForShutdown)
        {
            return;
        }

        _waitingForShutdown = true;
        await Vm.CancelAndWaitAsync();
        _shutdownAllowed = true;
        Close();
    }
}
