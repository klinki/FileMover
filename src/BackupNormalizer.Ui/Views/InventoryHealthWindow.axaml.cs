using Avalonia.Controls;
using BackupNormalizer.Ui.ViewModels;

namespace BackupNormalizer.Ui.Views;

public partial class InventoryHealthWindow : Window
{
    public InventoryHealthWindow(InventoryHealthViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
