using BackupNormalizer.Ui.ViewModels;
using BackupNormalizer.Ui.Views;

namespace BackupNormalizer.Tests;

[Collection("UI")]
public sealed class UiBackupCoverageTests
{
    [Fact]
    public async Task Coverage_Window_Filters_Results_And_Label_Changes_Invalidate_The_Report()
    {
        string directory = Path.Combine(
            AppContext.BaseDirectory,
            "bn-ui-coverage-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "inventory.db");
            using (var db = Database.OpenWritable(path, pooling: false))
            {
                db.UpsertRoot(
                    new StorageRootRow(
                        "r",
                        "Files",
                        directory,
                        true,
                        "Drive",
                        "sensitive",
                        Database.UtcNow()
                    )
                );
                File.WriteAllText(Path.Combine(directory, "a.txt"), "same");
                new Scanner(db, usnMode: "off").ScanRoot("r");
                new Scanner(db).HashNeeded("r");
            }
            var vm = new BackupCoverageViewModel();
            await vm.AddDatabaseAsync(path);
            await vm.Analyze();
            Assert.Single(vm.Entries);
            vm.Filter = "Every device";
            Assert.Single(vm.Entries);
            vm.Filter = "Unverified entries";
            Assert.Empty(vm.Entries);
            UiTestHost.Run(() =>
            {
                var window = new BackupCoverageWindow(vm);
                window.Show();
                Assert.Equal(vm, window.DataContext);
                window.Close();
            });
            vm.Sources[0].DeviceId = "Other label";
            Assert.Null(vm.Report);
            Assert.Empty(vm.Entries);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
