# Inventory search and selected details

Database-backed panels search entry names and recorded paths. Search uses the
selected root's recorded case sensitivity. A matching descendant keeps its
ancestor directories visible, so the result remains reachable through normal
folder navigation. The parent row stays available while search or a focused
filter is active.

The focused filters show all entries, unverified files, comparison conflicts,
scan errors, or links. Search, focused filters, and Differences only apply
together. These controls affect inventory browsing; live filesystem panels keep
their existing listing and staging behavior.

Selecting an inventory entry fills the persistent details pane with its name,
recorded absolute path, size, created and modified timestamps, fresh SHA-256 hash
availability and digest, comparison state, and entry kind. Links also show the
stored link target, resolved absolute target, and recorded notes. Clearing or
filtering out the selection clears its details.

The view-model behavior lives in
[FilePanelViewModel.Search.cs](../../../src/BackupNormalizer.Ui/ViewModels/FilePanelViewModel.Search.cs)
and [FilePanelViewModel.Inventory.cs](../../../src/BackupNormalizer.Ui/ViewModels/FilePanelViewModel.Inventory.cs).
The controls are [InventorySearchControl.axaml](../../../src/BackupNormalizer.Ui/Views/InventorySearchControl.axaml)
and [InventoryDetailsControl.axaml](../../../src/BackupNormalizer.Ui/Views/InventoryDetailsControl.axaml).
Focused tests are in
[UiInventorySearchTests.cs](../../../tests/BackupNormalizer.Tests/UiInventorySearchTests.cs).
