# Create an inventory from the desktop

Choose **Inventory → Create inventory...** while browsing a live folder or an
existing inventory. Select a folder or drive root, enter its root ID, and choose
a new SQLite database file. The database path defaults to the loaded JSON
configuration, and the folder defaults to the active panel.

**Hash files after scanning** is enabled by default. Turn it off for a metadata
scan only. Hashing starts after a complete scan and uses the configured worker
count. Symlinks and junctions are recorded without following or hashing targets.

**Create and scan** opens the existing jobs window with progress and cancellation.
Closing that window leaves the job running. Closing the app requests cancellation
and awaits the job. The resulting inventory appears in the panel that started it.
An incomplete or canceled scan remains browsable and can be rescanned through
**Inventory → Inventory jobs...** once its root is available.

Creation refuses existing databases and SQLite sidecar files. To rescan an
existing inventory, load its database and use **Inventory jobs...**. Loading a
config or opening the creation form does not create a database.

## JSON configuration

The desktop reads the same JSON format and defaults as the CLI on startup.
Discovery checks `BN_CONFIG`, then `settings.json` in the working directory,
then the legacy `backup-normalizer.json`. Missing default files use built-in
settings. Choose **File → Load configuration...** to select another JSON file.
Invalid JSON, unknown fields, invalid values, and invalid exclusion regexes leave
the previous desktop settings active and display an error.

The database setting fills the new-inventory form. Relative database paths resolve
from the working directory, matching the CLI. Jobs for an already loaded inventory
continue to use its selected database and root.

Both new and existing jobs use the config's hash algorithm, hash worker count,
MFT mode, USN mode, and exclusion rules. An omitted or null exclusion list reuses
saved root rules; an empty list clears them on rescan. Changing exclusions forces
a full scan through the core scanner. The desktop keeps its progress display even
when the config disables CLI console progress. Copy and trash settings do not
change inventory scans or the existing reviewed execution workflow.

See the [JSON configuration guide](../cli-config-exclusions/README.md) for the
format, scan modes, exclusions, and CLI override behavior.
