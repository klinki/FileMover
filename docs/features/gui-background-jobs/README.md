# Background inventory jobs

The jobs window offers a scan, a hash-needed pass, and a scan followed by hashing. It names the database, selected root, and recorded root path before work starts. The root path must be a fully qualified path available on this computer. A full-scan checkbox is available for scan actions. Hashing uses two workers by default.

`InventoryJobRunner` validates an existing database and root through a read-only connection before opening a writable connection. It uses the core scanner and hasher with the loaded JSON configuration's scan modes, exclusions, hash algorithm, and worker count. Progress includes the current path, scan counts or hash counts, elapsed time, and throughput. Updates are throttled so chunk-level hash progress does not flood the UI.

Choose **Inventory → Create inventory...** for a first scan without using the CLI.
See the [creation and configuration guide](../gui-initial-scan/README.md).

For scan-then-hash, the runner skips hashing if the scan returns errors or does not finish in the completed state. A hash job reports an incomplete result when regular files still lack current SHA-256 hashes. Cancellation is cooperative; the core scanner records a canceled scan as incomplete and the core hasher does not store a partial file hash.

Choose **Inventory → Inventory jobs...** to open the window for the active panel. Closing only the jobs window leaves the job running; reopening it shows current progress and the last result. Source and root selectors are disabled while a job runs. Closing the application cancels and awaits the job before closing.

Job completion reloads every open panel that points to the changed database, including two panels viewing the same file. Root availability is captured when loading or refreshing a snapshot; refresh after reconnecting a drive. The runner rechecks availability before any database writes.
