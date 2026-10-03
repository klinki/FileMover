# CLI JSON configuration and exclusions

The CLI automatically reads `settings.json` in its working directory when it
exists. Selection priority is `--config PATH`, then `BN_CONFIG`, then
`settings.json`, then the legacy `backup-normalizer.json` file. An explicit selection
must exist, except when creating it with `config init` or `init`. Missing default
files use built-in defaults. Invalid JSON, unknown fields, invalid regexes, and
invalid setting values produce an error.

```powershell
./BackupNormalizer.exe config init --config ./settings.json --db ./d.db
./BackupNormalizer.exe config show
./BackupNormalizer.exe scan d
./BackupNormalizer.exe hash --needed
./BackupNormalizer.exe scan d --config ./other-settings.json
```

`config init` creates only the JSON file and refuses to replace an existing file.
Without `--config` or `BN_CONFIG`, new config files use the name `settings.json`.
`init` initializes the database and writes its config as before. `config show`
prints the file's settings with built-in defaults filled in. Relative database
paths continue to resolve from the current working directory.

Example configuration:

```json
{
  "database": "./d.db",
  "hashAlgorithm": "sha256",
  "hashParallelism": 2,
  "mftMode": "auto",
  "usnMode": "auto",
  "noProgress": false,
  "excludedPathRegexes": [
    "(^|/)(\\.git|node_modules|cache)$",
    "\\.(tmp|bak)$"
  ]
}
```

Named roots still use `root add`. Register them once in the database. CLI value
options override corresponding `BN_*` environment values, which override JSON
defaults. For example, `--db` overrides `BN_DB` and `database`, and
`--parallelism` overrides `BN_PARALLELISM` and `hashParallelism`.
`--no-progress` disables output even when `noProgress` is false. `hashAlgorithm`
accepts SHA-256 or BLAKE3; this build maps BLAKE3 to its SHA-256 fallback.
The existing `copyParallelism` and `trashDirectoryName` fields remain accepted;
copy execution is serial, and the default executor trash directory is unchanged.

## Exclusion rules

Regexes match paths relative to the selected root, with `/` separators and no
leading slash. A matching directory name protects that directory's entire
subtree. A matching file is omitted. Patterns use .NET regex syntax and respect
the root's recorded case sensitivity; inline modifiers such as `(?i)` can
override it. A pattern matching a root-relative path or any of its parent paths
excludes that entry. Matching time is bounded. A regex timeout fails the scan
without declaring the inventory complete.

JSON requires escaped backslashes, so `"\\.tmp$"` means the regex `\.tmp$`.
PowerShell single quotes preserve the regex as written:

```powershell
./BackupNormalizer.exe scan d --db ./d.db `
  --exclude-path-regex '(^|/)(cache|node_modules)$' `
  --exclude-path-regex '\.tmp$'
```

Repeated `--exclude-path-regex` values replace the JSON exclusion list for that
scan. Use `--no-exclusions` to clear the root's rules, or set
`"excludedPathRegexes": []` in its configuration and scan again. Combining
`--no-exclusions` with regex flags is rejected.

Omitting `excludedPathRegexes`, or setting it to `null`, reuses the root's saved
rules. The database keeps them even if the config file is absent on another
computer. `status` reports these rules in text and JSON.

Recursive scans prune matching directories before reading their metadata or
enumerating their children. MFT and USN scans apply the same scope. Changing
rules forces a full scan and replaces the old journal checkpoint only after
success. Exclusions do not create scan errors. Genuine enumeration failures
still leave an incomplete inventory and preserve unseen entries.

After a successful rescan, previously indexed excluded paths become missing in
the inventory. They remain on disk. Hashing skips saved exclusions and any
additional config exclusions; it cannot clear saved rules without a rescan.
Removing rules brings files back through a full scan, and returning files lose
their old cached hashes.

New diff and automatic plan runs preserve paths excluded by either selected
root. Excluded files cannot supply move candidates or duplicate survivors, and
matching destinations remain untouched. Existing plans keep their recorded
operations, so regenerate a plan after changing exclusions. This feature does
not change manual staging of live folders.

Read-only databases from earlier versions have no exclusion rules and are not
migrated. Opening them writable applies the new policy-table migration.
