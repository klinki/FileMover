using BackupNormalizer.Ui.Models;

namespace BackupNormalizer.Ui.ViewModels;

public sealed partial class MainViewModel
{
    public InventoryHealthViewModel? CreateInventoryHealthViewModel(string side)
    {
        var panel = side == "Right" ? Right : Left;
        return panel.SelectedInventoryRoot is { HealthStatus: not null } root
            ? new InventoryHealthViewModel(root)
            : null;
    }
}
