using CommunityToolkit.Mvvm.ComponentModel;

namespace BackupNormalizer.Ui.ViewModels;

public sealed partial class MkdirDialogViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial string FolderName { get; set; } = "";
}
