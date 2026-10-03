using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BackupNormalizer.Ui.Models;

namespace BackupNormalizer.Ui.ViewModels;

public sealed partial class MainViewModel
{
    public bool CanExportInventory => Active.IsDatabase && !IsBusy;

    public async Task ExportInventoryAsync(string destination)
    {
        if (!CanExportInventory || Active.Snapshot is not { } snapshot) return;
        IsBusy = true;
        StatusMessage = "Exporting a portable inventory snapshot...";
        try
        {
            await Task.Run(() => Database.ExportSnapshot(snapshot.DatabasePath, destination));
            StatusMessage = "Portable inventory exported to " + Path.GetFullPath(destination);
        }
        catch (Exception ex) { StatusMessage = "Inventory export failed: " + ex.Message; }
        finally { IsBusy = false; }
    }

    public GuiSession CaptureSession(double leftFraction = 0.5,
        List<ColumnSession>? leftColumns = null, List<ColumnSession>? rightColumns = null) =>
        new(Capture(Left), Capture(Right), IsLeftActive, leftFraction, leftColumns, rightColumns);

    private static PanelSession Capture(FilePanelViewModel panel) => new(panel.Snapshot?.DatabasePath,
        panel.SelectedInventoryRoot?.Root.Id, panel.IsDatabase ? panel.InventoryPath : panel.CurrentPath);

    public async Task RestoreSessionAsync(GuiSession session)
    {
        if (!CanChangePanelSource) return;
        if (session == null || session.Left == null || session.Right == null)
        {
            StatusMessage = "Saved browsing session is invalid.";
            return;
        }
        var warnings = new List<string>();
        IsBusy = true;
        StatusMessage = "Restoring inventory panels...";
        try
        {
            await RestorePanel(Left, session.Left, warnings);
            await RestorePanel(Right, session.Right, warnings);
            IsLeftActive = session.IsLeftActive;
            StatusMessage = warnings.Count == 0 ? "Previous browsing session restored." : string.Join(" ", warnings);
        }
        finally { IsBusy = false; }
    }

    private async Task RestorePanel(FilePanelViewModel panel, PanelSession saved, List<string> warnings)
    {
        try
        {
            if (saved.DatabasePath is { Length: > 0 } database)
            {
                var snapshot = await Task.Run(() => InventorySnapshot.Load(database));
                string folder = saved.Folder ?? "";
                var root = System.Linq.Enumerable.FirstOrDefault(snapshot.Roots, r => r.Root.Id == saved.RootId);
                if (root == null)
                {
                    root = snapshot.Roots[0];
                    folder = "";
                    warnings.Add($"{panel.Side} inventory root '{saved.RootId}' is unavailable; using '{root.Display}' at its root folder.");
                }
                string requestedFolder = folder;
                while (folder.Length > 0 && (!root.Nodes.TryGetValue(folder, out var node) || !node.IsDirectory || node.IsLink))
                    folder = FilePanelViewModel.InventoryParent(folder);
                if (folder != requestedFolder)
                    warnings.Add($"{panel.Side} inventory folder '{requestedFolder}' is unavailable; using '{(folder.Length == 0 ? root.Root.Path : folder)}'.");
                panel.LoadSnapshot(snapshot, root.Root.Id, folder);
                return;
            }
            if (FilePanelViewModel.IsHostNativeAbsolutePath(saved.Folder) && Directory.Exists(saved.Folder))
                panel.UseFileSystem(saved.Folder);
            else warnings.Add($"{panel.Side} folder is unavailable; using the default folder.");
        }
        catch (Exception ex)
        {
            warnings.Add($"{panel.Side} panel could not be restored: {ex.Message}");
        }
    }
}
