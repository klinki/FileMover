# CLI JSON defaults and regex exclusions

The user selected CLI defaults only and root-relative regex matching with `/`
separators. Named root definitions remain in the inventory database.

The user also requested implicit `settings.json` loading. Explicit `--config`
takes priority, followed by `BN_CONFIG`, implicit `settings.json`, and the
legacy config filename. New config files default to `settings.json`.

- Document and validate the existing JSON defaults. Add `config show` and
  `config init`, explicit config-file errors, consistent `--config` selection,
  and correct use of hash algorithm and parallelism defaults.
- Add nullable `excludedPathRegexes` and `noProgress` settings. Scan accepts
  repeated `--exclude-path-regex` values and `--no-exclusions`. Explicit CLI
  regexes override the JSON list. An omitted list reuses the root's stored rules;
  an explicit empty list clears them on the next scan.
- Match root-relative paths and their ancestors, respecting the root's recorded
  case sensitivity. Validate regexes before starting a scan and bound matching
  time. Prune recursive directory traversal before metadata access and filter
  MFT and USN entries by the same rules.
- Persist each root's scan rules in a migrated table, with empty rules for
  legacy read-only databases. Rule changes force a full scan and clear the old
  USN checkpoint. Successful scans retire previously indexed excluded entries;
  failed scans retain unseen entries and remain unsuitable for planning.
- Hashing also honors stored rules. Matching, diff, and newly generated plans
  exclude paths protected by either selected root, including destination paths
  and duplicate move/trash candidates. Previously exported plans retain their
  recorded operations; regenerate them after changing scope.
- Verify config precedence and failures, repeated regexes, directory pruning,
  metadata errors on excluded paths, MFT-style enumeration, USN reuse and rule
  changes, hashing, migration compatibility, and planning preservation. Run the
  full suite in Release after focused checks.

Preserve unrelated changes. Initial delivery did not request commits or pushes.
The user requested a commit after verification on 2026-10-03. Do not push.
