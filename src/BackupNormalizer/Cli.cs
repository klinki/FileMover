namespace BackupNormalizer;

public static class Cli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args is ["--help"] or ["help"] or ["-h"])
        {
            return Help();
        }
        // global flags
        Log.Json = args.Contains("--json");
        try
        {
            args = NormalizeConfigOption(args);
            if (args.Length == 0)
            {
                return Help();
            }

            string cmd = args[0].ToLowerInvariant();
#if NATIVE_AOT
            if (cmd is "plan" or "verify" or "purge" or "diff" or "coverage" or "location-changes")
                return Fail(
                    $"'{cmd}' is not available in the NAS AOT build. Prepare plans and comparisons on the PC."
                );
#endif
            return cmd switch
            {
                "--version" or "version" => Version(args[1..]),
                "init" => Init(args[1..]),
                "config" => Config(args[1..]),
                "help" or "--help" or "-h" => Help(),
                "root" => Root(args[1..]),
                "scan" => Scan(args[1..]),
                "status" => Status(args[1..]),
                "db" => Db(args[1..]),
                "hash" => Hash(args[1..]),
                "execute" => Execute(args[1..]),
#if !NATIVE_AOT
                "plan" => Plan(args[1..]),
                "verify" => Verify(args[1..]),
                "purge" => Purge(args[1..]),
                "diff" => Diff(args[1..]),
                "coverage" => Coverage(args[1..]),
                "location-changes" => LocationChanges(args[1..]),
#endif
                "db-test" => DbTest(args[1..]),
                "scan-test" => ScanTest(args[1..]),
                _ => Fail($"unknown command '{args[0]}'. Try 'help'."),
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            return 2;
        }
    }

    private static int Help()
    {
#if NATIVE_AOT
        Console.WriteLine(
            $"""
            backup-normalizer {BuildInfo.FromAssembly(
                typeof(Cli).Assembly
            ).ShortVersion} — NAS inventory and execution
              init [--db PATH] [--config PATH]
              config init|show [--config PATH]
              root add <id> <path> [--name N] [--writable true|false] [--db PATH]
              root list [--db PATH]
              scan <rootId|--all> [--db PATH] [--mft off] [--usn off] [--full] [--no-progress]
                   [--exclude-path-regex REGEX ... | --no-exclusions]
              scan errors <rootId> [--scan ID] [--db PATH] [--json]
              hash <rootId> [--all] | hash --needed [--db PATH] [--parallelism N] [--no-progress]
              status [rootId] [--db PATH] [--hash-algo ALGORITHM] [--json]
              db export --db SOURCE --output DESTINATION [--json]
              execute <plan-id> [--db PATH] [--source-path PATH] [--target-path PATH]
                      [--yes] [--resume] [--stop-on-error]
              db-test | scan-test <path> | --version [--json]
            The matching migration helper must remain beside this executable.
            Prepare plans on the PC and transfer a portable plan database to the NAS.
            Comparison and plan creation are available in the Windows application.
            --config PATH selects JSON defaults. CLI values override BN_* variables and JSON defaults.
            Stored exclusion rules apply to scans and hashing. Links are recorded without following targets.
            """
        );
        return 0;
#else
        Console.WriteLine(
            $"""
            backup-normalizer {BuildInfo.FromAssembly(
                typeof(Cli).Assembly
            ).ShortVersion} — per-drive source/target comparison
              Usage: BackupNormalizer <command> [options]
              init [--db PATH] [--config PATH]
              config init|show [--config PATH]
              root add <id> <path> [--name N] [--writable true|false] [--db PATH]
              root list [--db PATH]
              scan <rootId|--all> [--db PATH] [--mft off|auto|require] [--usn auto|off] [--full] [--no-progress]
                   [--exclude-path-regex REGEX ... | --no-exclusions]
              scan errors <rootId> [--scan ID] [--db PATH] [--json]
              status [rootId] [--db PATH] [--hash-algo ALGORITHM] [--json]
              db export --db SOURCE --output DESTINATION [--json]
              hash <rootId> --all | hash --needed [--db PATH] [--parallelism N] [--no-progress]
              plan --source-db S.db --source-root R --target-db T.db --target-root R [--plan ID]
              plan show <plan-id> [--db PATH] | plan export <plan-id> [--format json] [--output F] [--db PATH]
              plan import <plan.json> [--db PATH] [--target-path ABS]
              plan conflicts <plan-id> [--db PATH]
              execute <plan-id> [--db PATH] [--source-path ABS] [--target-path ABS] [--resume] [--stop-on-error] [--yes]
              verify <plan-id> [--db PATH] [--source-path ABS] [--target-path ABS]
              purge --older-than 30d --yes [--db PATH] [--path ROOTPATH]
              diff --source-db S.db --source-root R --target-db T.db --target-root R
              coverage --inventory DEVICE=PATH [--inventory DEVICE=PATH ...] [--json]
              location-changes --source-db A.db --source-root R --target-db B.db --target-root R
                   [--filter quick-differences|content-changed|changes|moved|copied|removed-copies|ambiguous|only-in-a|only-in-b|unverified|unchanged|all-differences|all]
                   [--json | --format csv|json] [--output PATH]
                   [--match-filenames] [--extensions zip,mp4]
                   [--exclude-path RELATIVE_PATH ...]
                   Grouped views: --filter filename-differences|duplicates-in-a|duplicates-in-b
              db-test [--db PATH] | scan-test <path> | --version
            Each database can inventory one drive with multiple named roots. Select the source and target
            for each diff or plan. Automatic plans require fully scanned, disjoint roots.
            --mft auto uses fast NTFS direct enumeration on Windows (needs NTFS + admin, else falls back);
            --mft require fails loudly instead. --elevate restarts the app elevated via UAC when needed.
            --usn auto refreshes changed entries from an existing NTFS journal after a complete baseline scan.
            --full forces full enumeration; --usn off disables journal checkpoints. Unavailable journals use full scans.
            --config PATH selects JSON defaults for any command, before or after the command name.
            Config priority: --config PATH, then BN_CONFIG, then ./settings.json, then legacy ./backup-normalizer.json.
            CLI values override BN_* variables and JSON defaults.
            Exclusion regexes match root-relative paths using / and protect matching directory subtrees.
            Omitted exclusions reuse the root's stored rules. Changing rules forces a full scan.
            """
        );
        return 0;
#endif
    }

    private static int Version(string[] a)
    {
        var build = BuildInfo.FromAssembly(typeof(Cli).Assembly);
        if (Has(a, "--json"))
        {
            WriteJson(build, CliJsonContext.Default.BuildInfo);
        }
        else
        {
            Console.WriteLine(build);
        }

        return 0;
    }

    private static void WriteJson<T>(
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo
    ) => Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(value, typeInfo));

    private static string TerminalText(string value) =>
        new(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    private static int Status(string[] a)
    {
        var config = LoadConfig(a);
        using var db = Database.OpenReadOnly(Opt(a, "--db", config.Database));
        string algorithm = Opt(a, "--hash-algo", config.HashAlgorithm);
        string? selectedRoot = a.Length > 0 && !a[0].StartsWith("--") ? a[0] : null;
        var roots =
            selectedRoot == null ? db.ListRoots().Select(r => r.Id).ToList() : [selectedRoot];
        var statuses = roots.Select(r => db.GetInventoryStatus(r, algorithm)).ToList();
        if (Has(a, "--json"))
        {
            WriteJson(statuses, CliJsonContext.Default.ListInventoryStatusRow);
        }
        else
        {
            foreach (var status in statuses)
            {
                Console.WriteLine(TerminalText($"root {status.Root.Id}: {status.Root.Path}"));
                var latest = status.LatestScan;
                Console.WriteLine(
                    TerminalText(
                        latest == null
                            ? "  scan: none"
                            : $"  scan #{latest.Scan.Id}: {latest.Scan.Status}, {latest.Mode ?? "mode unknown"}, {latest.ScannedCount?.ToString() ?? "unknown"} entries, {latest.ErrorCount?.ToString() ?? "unknown"} errors"
                    )
                );
                Console.WriteLine(
                    $"  last successful scan: {status.LastSuccessfulScan?.CompletedUtc ?? "none"}"
                );
                Console.WriteLine(
                    $"  files: {status.RegularFiles}; skipped links: {status.Links}; missing entries: {status.MissingEntries}; entry errors: {status.EntryErrors}"
                );
                Console.WriteLine(
                    $"  {TerminalText(algorithm)} hashes: {status.UsableHashes} usable, {status.MissingHashes} needed"
                );
                foreach (string pattern in status.ExcludedPathRegexes ?? [])
                {
                    Console.WriteLine(TerminalText($"  excluded path regex: {pattern}"));
                }

                Console.WriteLine(
                    $"  USN checkpoint: {(status.Checkpoint == null ? "none; next scan uses full enumeration" : "stored; checked against the journal on the next scan")}"
                );
                if (latest?.FallbackReason != null)
                {
                    Console.WriteLine(TerminalText($"  fallback: {latest.FallbackReason}"));
                }

                Console.WriteLine(
                    TerminalText(
                        $"  planning: {(status.PlanningReady ? "ready for content planning" : status.BlockingReason)}"
                    )
                );
                foreach (var error in status.Errors)
                {
                    Console.WriteLine(TerminalText($"  ERROR \"{error.Path}\": {error.Message}"));
                }

                if (latest != null && latest.ErrorCount == null)
                {
                    Console.WriteLine(
                        "  Historical scan diagnostics are unavailable. Rescan to record details."
                    );
                }
            }
        }

        return statuses.All(s => s.PlanningReady) ? 0 : 3;
    }

    private static int LocationChanges(string[] args)
    {
        string sourceDb = Opt(args, "--source-db", ""),
            sourceRoot = Opt(args, "--source-root", "");
        string targetDb = Opt(args, "--target-db", ""),
            targetRoot = Opt(args, "--target-root", "");
        if (new[] { sourceDb, sourceRoot, targetDb, targetRoot }.Any(string.IsNullOrWhiteSpace))
            return Fail(
                "location-changes requires --source-db A.db --source-root R --target-db B.db --target-root R"
            );
        string filter = FileLocationChanges.NormalizeFilter(
            Opt(args, "--filter", "quick-differences")
        );
        string format = Opt(args, "--format", "json").ToLowerInvariant();
        if (format is not ("csv" or "json") || (Has(args, "--json") && format != "json"))
            return Fail(
                "location-changes supports csv or json; --json cannot be combined with --format csv"
            );
        var excludedPaths = new List<string>();
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] != "--exclude-path")
                continue;
            if (++index >= args.Length || args[index].StartsWith("--"))
                return Fail("--exclude-path requires a root-relative file or directory path");
            excludedPaths.Add(args[index]);
        }
        var report = FileLocationChanges.Analyze(
            new(sourceDb, sourceRoot),
            new(targetDb, targetRoot),
            new FileDifferenceOptions(
                Has(args, "--match-filenames"),
                Opt(args, "--extensions", ".zip,.mp4"),
                excludedPaths
            )
        );
        if (filter == GroupedFileReports.FilenameDifferences && !report.FilenameMatchingEnabled)
            return Fail("--filter filename-differences requires --match-filenames");
        if (Has(args, "--output"))
        {
            string path = Opt(args, "--output", "");
            if (string.IsNullOrWhiteSpace(path))
                return Fail("--output requires a file path");
            LocationChangesExport.Save(path, report, format, filter);
            Console.WriteLine(
                $"Location changes report saved to {TerminalText(Path.GetFullPath(path))}"
            );
        }
        else if (Has(args, "--json") || Has(args, "--format"))
        {
            LocationChangesExport.Write(Console.Out, report, format, filter);
        }
        else
        {
            Console.WriteLine($"Recorded file differences A → B | {filter}");
            Console.WriteLine(
                TerminalText(
                    $"A: {report.A.Input.DatabasePath} [{report.A.Input.RootId}] {report.A.RootPath} | scanned {report.A.ScannedUtc ?? "unknown"}"
                )
            );
            Console.WriteLine(
                TerminalText(
                    $"B: {report.B.Input.DatabasePath} [{report.B.Input.RootId}] {report.B.RootPath} | scanned {report.B.ScannedUtc ?? "unknown"}"
                )
            );
            Console.WriteLine(
                string.Join("; ", report.Summary.Select(item => $"{item.Key}: {item.Value}"))
            );
            Console.WriteLine(
                $"Unverified files: {report.UnverifiedFiles}; unverified groups: {report.Summary.GetValueOrDefault(FileLocationChanges.Unverified)}"
            );
            if (report.ExcludedPaths.Count > 0)
                Console.WriteLine(
                    "Excluded paths in both inventories: "
                        + TerminalText(string.Join(", ", report.ExcludedPaths))
                );
            if (GroupedFileReports.IsGroupedView(filter))
            {
                WriteGroupedFileReport(report, filter);
                return 0;
            }
            foreach (var group in FileLocationChanges.Filter(report, filter))
            {
                Console.WriteLine(
                    group.ContentComparison is { } comparison
                        ? $"{group.Classification} | {comparison.BeforeSize:N0} → {comparison.AfterSize:N0} bytes"
                        : $"{group.Classification} | {group.Size:N0} bytes | {group.Digest ?? "unavailable"}"
                );
                foreach (var location in group.Locations)
                    Console.WriteLine(
                        TerminalText(
                            $"  {location.Side} {location.State}: {location.RelativePath} | {location.Size ?? group.Size:N0} bytes | SHA-256: {location.Digest ?? group.Digest ?? "unavailable"}"
                        )
                    );
                if (group.VerificationReason != null)
                    Console.WriteLine(TerminalText("  " + group.VerificationReason));
            }
        }
        return 0;
    }

    private static void WriteGroupedFileReport(LocationChangesReport report, string filter)
    {
        if (filter == GroupedFileReports.FilenameDifferences)
        {
            Console.WriteLine(
                $"Filename extensions: {string.Join(", ", report.FilenameExtensions)} | Groups: {report.FilenameGroups.Count:N0}"
            );
            foreach (var family in report.FilenameGroups)
            {
                Console.WriteLine(
                    TerminalText(
                        $"{family.Filename} | {family.Classification} | verified versions A/B: {family.VerifiedVersionsA}/{family.VerifiedVersionsB} | files A/B: {family.CopiesA}/{family.CopiesB} | unverified: {family.UnverifiedFiles}"
                    )
                );
                foreach (var version in family.Versions)
                {
                    Console.WriteLine(
                        $"  {version.State} | {version.Size:N0} bytes | copies A/B: {version.CopiesA}/{version.CopiesB} | SHA-256: {version.Digest ?? "unavailable"}"
                    );
                    if (version.VerificationReason != null)
                        Console.WriteLine(TerminalText("  " + version.VerificationReason));
                    foreach (var location in version.Locations)
                        Console.WriteLine(
                            TerminalText($"    {location.Side}: {location.RelativePath}")
                        );
                }
            }
            return;
        }
        string side = filter == GroupedFileReports.DuplicatesInA ? "A" : "B";
        var duplicates = side == "A" ? report.DuplicatesA : report.DuplicatesB;
        Console.WriteLine(
            $"Inventory {side} only | verified duplicate groups: {duplicates.Count:N0}"
        );
        foreach (var group in duplicates)
        {
            Console.WriteLine(
                $"{group.Size:N0} bytes | {group.Copies:N0} copies | {group.ExtraCopies:N0} extra | potential savings: {group.PotentialSavingsBytes:N0} bytes | SHA-256: {group.Digest}"
            );
            foreach (var location in group.Locations)
                Console.WriteLine(TerminalText("  " + location.RelativePath));
        }
        foreach (var file in report.UnverifiedLocations.Where(f => f.Location.Side == side))
            Console.WriteLine(
                TerminalText($"Unverified: {file.Location.RelativePath} | {file.Reason}")
            );
    }

    private static int Coverage(string[] args)
    {
        var inventories = new List<(string Device, string Path)>();
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] == "--json")
            {
                continue;
            }

            if (args[index] != "--inventory" || ++index >= args.Length)
            {
                return Fail("coverage requires repeated --inventory DEVICE=DATABASE arguments");
            }

            int separator = args[index].IndexOf('=');
            if (separator <= 0 || separator == args[index].Length - 1)
            {
                return Fail("--inventory requires DEVICE=DATABASE");
            }

            inventories.Add((args[index][..separator], args[index][(separator + 1)..]));
        }
        var inputs = new List<CoverageInput>();
        foreach (var inventory in inventories)
        {
            using var db = Database.OpenReadOnly(inventory.Path, pooling: false);
            inputs.AddRange(
                db.ListRoots()
                    .Select(root => new CoverageInput(inventory.Path, root.Id, inventory.Device))
            );
        }
        var report = BackupCoverage.Analyze(inputs);
        if (Has(args, "--json"))
        {
            WriteJson(report, CliJsonContext.Default.CoverageReport);
            return 0;
        }
        Console.WriteLine(
            $"Snapshot coverage: {report.Devices.Count} device labels, {report.Content.Count} verified content groups"
        );
        Console.WriteLine(
            $"Only one device: {report.SingleDeviceContent}; on every device: {report.ContentOnEveryDevice}; unverified entries: {report.Unverified.Count}"
        );
        Console.WriteLine(
            "Recorded inventories, not a live verification. Assign the same label to roots and exports from the same physical device."
        );
        foreach (var source in report.Sources)
        {
            Console.WriteLine(
                TerminalText(
                    $"{source.Input.DeviceId}: {source.Input.DatabasePath} [{source.Input.RootId}] | {source.ScanStatus ?? "not scanned"} | {BackupCoverage.ScanAge(source.ScannedUtc)} | {(source.LocallyAvailable ? "available locally" : "offline / not available locally")}"
                )
            );
        }

        foreach (var content in report.Content)
        {
            Console.WriteLine(
                $"{content.DeviceCount}/{report.Devices.Count} devices | {content.Size:N0} bytes | SHA-256 {content.Digest}"
            );
            foreach (var location in content.Locations)
            {
                Console.WriteLine(
                    TerminalText(
                        $"  {location.DeviceId} [{location.RootId}] {location.RelativePath}"
                    )
                );
            }
        }
        foreach (var unknown in report.Unverified)
        {
            Console.WriteLine(
                TerminalText(
                    $"UNVERIFIED: {unknown.Location.DeviceId} [{unknown.Location.RootId}] {unknown.Location.RelativePath}: {unknown.Reason}"
                )
            );
        }

        return 0;
    }

    private static int ScanErrors(string[] a)
    {
        if (a.Length == 0 || a[0].StartsWith("--"))
        {
            return Fail("scan errors requires a root ID");
        }

        using var db = Database.OpenReadOnly(Opt(a, "--db", LoadConfig(a).Database));
        long? scanId = null;
        if (Has(a, "--scan"))
        {
            if (!long.TryParse(Opt(a, "--scan", ""), out long requested) || requested <= 0)
            {
                return Fail("--scan requires a positive scan ID");
            }

            scanId = requested;
        }
        if (db.GetRoot(a[0]) == null)
        {
            return Fail($"unknown root '{a[0]}'");
        }

        var details = db.GetScanDetails(a[0], scanId);
        if (details == null)
        {
            return Fail("scan not found for this root");
        }

        var errors = db.ListScanDiagnostics(details.Scan.Id);
        if (Has(a, "--json"))
        {
            WriteJson(new ScanErrorsJson(details, errors), CliJsonContext.Default.ScanErrorsJson);
        }
        else
        {
            Console.WriteLine(
                TerminalText($"scan {a[0]} #{details.Scan.Id}: {details.Scan.Status}")
            );
            if (details.ErrorCount == null)
            {
                Console.WriteLine(
                    "Historical scan diagnostics are unavailable. Rescan to record details."
                );
            }
            else if (errors.Count == 0)
            {
                Console.WriteLine("No recorded scan errors.");
            }

            foreach (var error in errors)
            {
                Console.WriteLine(
                    TerminalText($"{error.RecordedUtc} ERROR \"{error.Path}\": {error.Message}")
                );
            }
        }
        return 0;
    }

    private static int Db(string[] a)
    {
        if (a.Length == 0 || a[0] != "export")
        {
            return Fail("db requires the export subcommand");
        }

        string destination = Opt(a, "--output", "");
        if (string.IsNullOrWhiteSpace(destination))
        {
            return Fail("db export requires --output");
        }

        string source = Opt(a, "--db", LoadConfig(a).Database);
        Database.ExportSnapshot(source, destination);
        if (Has(a, "--json"))
        {
            WriteJson(
                new DatabaseExportJson(Path.GetFullPath(source), Path.GetFullPath(destination)),
                CliJsonContext.Default.DatabaseExportJson
            );
        }
        else
        {
            Console.WriteLine(
                TerminalText($"exported inventory snapshot: {Path.GetFullPath(destination)}")
            );
        }

        return 0;
    }

    private static int Fail(string m)
    {
        Console.Error.WriteLine("error: " + m);
        return 2;
    }

    private static string Opt(string[] a, string name, string fallback)
    {
        for (int i = 0; i < a.Length - 1; i++)
        {
            if (a[i] == name)
            {
                return a[i + 1];
            }
        }

        var env = Environment.GetEnvironmentVariable(
            "BN_" + name.TrimStart('-').ToUpperInvariant().Replace('-', '_')
        );
        return env ?? fallback;
    }

    private static bool Has(string[] a, string name) => a.Contains(name);

    private static string[] NormalizeConfigOption(string[] args)
    {
        var remaining = new List<string>();
        string? config = null;
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] != "--config")
            {
                remaining.Add(args[index]);
                continue;
            }
            if (config != null)
            {
                throw new ArgumentException("--config may only be specified once.");
            }

            if (++index >= args.Length || args[index].StartsWith("--"))
            {
                throw new ArgumentException("--config requires a file path.");
            }

            config = args[index];
        }
        if (config != null)
        {
            remaining.AddRange(["--config", config]);
        }

        return remaining.ToArray();
    }

    private static AppConfig LoadConfig(string[] a, bool allowMissing = false)
    {
        return AppConfig.Load(
            Has(a, "--config") ? Opt(a, "--config", AppConfig.DefaultPath) : null,
            allowMissing
        );
    }

    private static int Config(string[] a)
    {
        if (a.Length == 0)
        {
            return Fail("config init|show [--config PATH]");
        }

        if (a[0] == "show")
        {
            WriteJson(LoadConfig(a), CliJsonContext.Default.AppConfig);
            return 0;
        }
        if (a[0] != "init")
        {
            return Fail("config init|show [--config PATH]");
        }

        string path = Opt(a, "--config", AppConfig.DefaultPath);
        var config = new AppConfig();
        config.Database = Opt(a, "--db", config.Database);
        config.Save(path, overwrite: false);
        Console.WriteLine(TerminalText($"created config: {Path.GetFullPath(path)}"));
        return 0;
    }

    private static IReadOnlyList<string>? ScanExclusions(string[] a, AppConfig config)
    {
        var patterns = new List<string>();
        for (int index = 0; index < a.Length; index++)
        {
            if (a[index] != "--exclude-path-regex")
            {
                continue;
            }

            if (++index >= a.Length || a[index].StartsWith("--"))
            {
                throw new ArgumentException("--exclude-path-regex requires a regex.");
            }

            patterns.Add(a[index]);
        }
        if (Has(a, "--no-exclusions"))
        {
            if (patterns.Count > 0)
            {
                throw new ArgumentException(
                    "Use --no-exclusions or --exclude-path-regex, not both."
                );
            }

            return [];
        }
        return patterns.Count > 0
            ? new PathExclusions(patterns).Patterns
            : config.ExcludedPathRegexes;
    }

    private static int Init(string[] a)
    {
        string db = Opt(a, "--db", LoadConfig(a, allowMissing: true).Database);
        using var _ = new Database(db);
        var cfg = LoadConfig(a, allowMissing: true);
        cfg.Database = db;
        cfg.Save(Opt(a, "--config", AppConfig.DefaultPath));
        Console.WriteLine($"initialized {db}");
        return 0;
    }

    private static int Root(string[] a)
    {
        if (a.Length == 0)
        {
            return Fail("root add|list");
        }

        string db = Opt(a, "--db", LoadConfig(a).Database);
        if (a[0] == "list")
        {
            using var d = Database.OpenReadOnly(db);
            foreach (var r in d.ListRoots())
            {
                Console.WriteLine($"{r.Id}\t{r.Name}\t{r.Path}\twritable={r.Writable}");
            }

            return 0;
        }
        if (a[0] == "add" && a.Length >= 3)
        {
            string id = a[1],
                path = Path.GetFullPath(a[2]);
            string name = Opt(a, "--name", id);
            bool writable = !string.Equals(
                Opt(a, "--writable", "true"),
                "false",
                StringComparison.OrdinalIgnoreCase
            );
            if (!Directory.Exists(path))
            {
                return Fail($"path not found: {path}");
            }

            using var d = new Database(db);
            d.UpsertRoot(
                new StorageRootRow(
                    id,
                    name,
                    path,
                    writable,
                    Paths.GetFileSystemId(path),
                    Paths.DetectCaseSensitivity(path),
                    Database.UtcNow()
                )
            );
            Console.WriteLine($"root '{id}' -> {path}");
            return 0;
        }
        return Fail("root add <id> <path> [--name N] [--writable true|false] | root list");
    }

    private static int Scan(string[] a)
    {
        if (a.Length > 0 && a[0] == "errors")
        {
            return ScanErrors(a[1..]);
        }

        if (a.Length == 0)
        {
            return Fail("scan <rootId|--all>");
        }

        var config = LoadConfig(a);
        string db = Opt(a, "--db", config.Database);
        string algo = Opt(a, "--hash-algo", config.HashAlgorithm);
        string mft = Opt(a, "--mft", config.MftMode);
        string usn = Opt(a, "--usn", config.UsnMode);
        var exclusions = ScanExclusions(a, config);
        using var d = new Database(db);
        var sc = new Scanner(d, algo, mft, usn, exclusions);
        bool showProgress =
            !Has(a, "--no-progress") && !config.NoProgress && !Console.IsOutputRedirected;
        int totalErrors = 0;
        int RunRootErrors(string rootId, string label, out int scanned)
        {
            int estimate = 0;
            try
            {
                estimate = d.CountFiles(rootId);
            }
            catch { }
            ScanProgressRenderer? renderer = showProgress
                ? new ScanProgressRenderer(label, estimate)
                : null;
            var scanErrors = new List<ScanError>();
            try
            {
                var (s, e) = sc.ScanRoot(rootId, renderer, scanErrors.Add, full: Has(a, "--full"));
                scanned = s;
                return e;
            }
            finally
            {
                renderer?.Finish();
                if (showProgress && sc.LastScanFallbackReason != null)
                {
                    Log.Info(
                        new string(
                            $"scan {rootId}: {sc.LastScanFallbackReason}"
                                .Select(c => char.IsControl(c) ? ' ' : c)
                                .ToArray()
                        )
                    );
                }

                foreach (var error in scanErrors)
                {
                    string message =
                        $"scan {error.RootId}: ERROR \"{error.Path}\": {error.Message}";
                    Console.Error.WriteLine(
                        new string(message.Select(c => char.IsControl(c) ? ' ' : c).ToArray())
                    );
                }
            }
        }
        if (a[0] == "--all")
        {
            foreach (var r in d.ListRoots())
            {
                int rootErrors = RunRootErrors(r.Id, r.Id, out int rootScanned);
                totalErrors += rootErrors;
                Console.WriteLine(Summary(r.Id, rootScanned, rootErrors));
            }
            return totalErrors == 0 ? 0 : 3;
        }
        int errors = RunRootErrors(a[0], a[0], out int scanned);
        Console.WriteLine(Summary(a[0], scanned, errors));
        return errors == 0 ? 0 : 3;
        string Summary(string id, int count, int issues) =>
            $"scan {id}: {count} {(sc.LastScanWasIncremental ? "entries refreshed using USN" : "entries")}, {issues} errors ({(issues == 0 ? "complete" : "incomplete")})";
    }

    /// <summary>Single-line TTY progress; silent when output is redirected.</summary>
    internal sealed class ScanProgressRenderer : IProgress<ScanProgress>
    {
        private readonly string _label;
        private readonly int _estimate;
        private readonly TextWriter _output;
        private readonly Func<int> _windowWidth;
        private ScanProgress? _latest;
        private TimeSpan? _lastUpdate;
        private int _lineWidth;
        private bool _finished;

        public ScanProgressRenderer(
            string label,
            int estimate,
            TextWriter? output = null,
            Func<int>? windowWidth = null
        )
        {
            _label = label;
            _estimate = estimate;
            _output = output ?? Console.Out;
            _windowWidth = windowWidth ?? (() => Console.WindowWidth);
        }

        public void Report(ScanProgress p)
        {
            if (_finished)
            {
                return;
            }

            _latest = p;
            if (
                _lastUpdate.HasValue
                && p.Elapsed >= _lastUpdate.Value
                && p.Elapsed - _lastUpdate.Value < TimeSpan.FromMilliseconds(200)
            )
            {
                return;
            }

            Render(p);
        }

        private void Render(ScanProgress p)
        {
            _lastUpdate = p.Elapsed;
            try
            {
                int columns = _windowWidth();
                int width = columns > 1 ? columns - 1 : 120;
                string pct =
                    !p.Incremental && _estimate > 0
                        ? $" ({Math.Min(99, 100L * p.Scanned / _estimate)}% of ~{_estimate:N0})"
                        : "";
                double rate = p.Elapsed.TotalSeconds > 0 ? p.Scanned / p.Elapsed.TotalSeconds : 0;
                string dir = p.CurrentPath.Length > 40 ? "…" + p.CurrentPath[^39..] : p.CurrentPath;
                string line =
                    $"scan {_label}: {p.Scanned:N0} {(p.Incremental ? "entries refreshed using USN" : "entries")}{pct} | {rate:N0}/s | {p.Elapsed:mm\\:ss} | {dir}";
                line = new string(line.Select(c => char.IsControl(c) ? ' ' : c).ToArray());

                // Reserve two cells for non-ASCII characters so wide paths cannot wrap.
                int length = 0,
                    cells = 0;
                foreach (char c in line)
                {
                    int cellWidth = char.IsAscii(c) ? 1 : 2;
                    if (cells + cellWidth > width)
                    {
                        break;
                    }

                    cells += cellWidth;
                    length++;
                }
                if (length > 0 && char.IsHighSurrogate(line[length - 1]))
                {
                    length--;
                    cells -= 2;
                }
                int padding = Math.Max(0, Math.Min(_lineWidth, width) - cells);
                _output.Write("\r" + line[..length] + new string(' ', padding));
                _lineWidth = cells;
            }
            catch { }
        }

        public void Finish()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            if (_latest != null)
            {
                Render(_latest);
            }

            try
            {
                _output.WriteLine();
            }
            catch { }
        }
    }

    private static int Hash(string[] a)
    {
        bool needed = Has(a, "--needed");
        string? rootId = a.Length > 0 && !a[0].StartsWith("--") ? a[0] : null;
        if (!needed && rootId == null)
        {
            return Fail("hash --needed | hash <rootId> --all");
        }

        var config = LoadConfig(a);
        string db = Opt(a, "--db", config.Database);
        if (
            !int.TryParse(Opt(a, "--parallelism", config.HashParallelism.ToString()), out int par)
            || par < 1
        )
        {
            return Fail("--parallelism must be a positive integer");
        }

        using var d = new Database(db);
        var sc = new Scanner(
            d,
            Opt(a, "--hash-algo", config.HashAlgorithm),
            excludedPathRegexes: config.ExcludedPathRegexes
        );
        var renderer =
            !Has(a, "--no-progress") && !config.NoProgress && !Console.IsOutputRedirected
                ? new HashProgressRenderer(needed ? "--needed" : rootId!)
                : null;
        (int hashed, int skipped, int unstable) result;
        try
        {
            result = sc.HashNeeded(
                needed ? null : rootId,
                !needed && Has(a, "--all"),
                par,
                renderer
            );
        }
        finally
        {
            renderer?.Finish();
        }
        Console.WriteLine(
            $"hash {(needed ? "--needed" : rootId)}: {result.hashed} hashed, {result.skipped} {(needed ? "reused" : "skipped")}, {result.unstable} unstable"
        );
        return 0;
    }

    /// <summary>Synchronous callbacks avoid progress writes after the completion summary.</summary>
    private sealed class HashProgressRenderer(string label) : IProgress<HashProgress>
    {
        private TimeSpan? _lastUpdate;
        private int _lineLength;

        public void Report(HashProgress p)
        {
            // The scanner serializes callbacks; only terminal writes need throttling.
            if (
                _lastUpdate.HasValue
                && p.Processed < p.TotalFiles
                && p.Elapsed - _lastUpdate.Value < TimeSpan.FromMilliseconds(200)
            )
            {
                return;
            }

            _lastUpdate = p.Elapsed;
            long percent = p.TotalFiles > 0 ? 100L * p.Processed / p.TotalFiles : 100;
            double mib = p.BytesRead / (1024.0 * 1024);
            double speed = p.Elapsed.TotalSeconds > 0 ? mib / p.Elapsed.TotalSeconds : 0;
            string line =
                $"hash {label}: {p.Processed:N0}/{p.TotalFiles:N0} files ({percent}%) | {mib:N1} MiB, {speed:N0} MiB/s | {p.Elapsed:hh\\:mm\\:ss}";
            try
            {
                int columns = Console.WindowWidth;
                int width = columns > 1 ? columns - 1 : 120;
                int pathWidth = width - line.Length - 3;
                if (p.CurrentPath.Length > 0 && pathWidth > 1)
                {
                    string path =
                        p.CurrentPath.Length <= pathWidth
                            ? p.CurrentPath
                            : "…" + p.CurrentPath[^(pathWidth - 1)..];
                    line += " | " + path;
                }
                if (line.Length > width)
                {
                    line = line[..width];
                }

                Console.Write("\r" + line.PadRight(Math.Min(_lineLength, width)));
                _lineLength = line.Length;
            }
            catch { }
        }

        public void Finish()
        {
            try
            {
                Console.WriteLine();
            }
            catch { }
        }
    }

    private static int Plan(string[] a)
    {
        if (a.Length == 0)
        {
            return Fail("plan ...");
        }

        var config = LoadConfig(a);
        if (a[0] == "show" && a.Length >= 2)
        {
            string db = Opt(a, "--db", config.Database);
            using var d = new Database(db);
            var doc = new Planner(d).ExportPlan(a[1]);
            Console.WriteLine(
                $"Plan {doc.PlanId} created {doc.CreatedUtc} estBytes={doc.EstimatedBytesCopied}"
            );
            Console.WriteLine($"Source: {doc.SourceRoot} at {doc.SourcePath}");
            Console.WriteLine($"Target: {doc.TargetRoot} at {doc.TargetPath}");
            foreach (var o in doc.Operations)
            {
                Console.WriteLine(
                    $"  {o.Id:D4} {o.Type, -7} {o.SourceKind}:{o.SourceRoot}:{o.SourcePath} -> {o.DestinationRoot}:{o.DestinationPath} size={o.ExpectedSize}"
                        + (o.SkipReason == null ? "" : $" reason={o.SkipReason}")
                );
            }

            return 0;
        }
        if (a[0] == "export" && a.Length >= 2)
        {
            string db = Opt(a, "--db", config.Database);
            using var d = new Database(db);
            var doc = new Planner(d).ExportPlan(a[1]);
            string json = Planner.ToJson(doc);
            string? outF = Has(a, "--output") ? Opt(a, "--output", "") : null;
            if (!string.IsNullOrEmpty(outF))
            {
                File.WriteAllText(outF, json);
            }
            else
            {
                Console.WriteLine(json);
            }

            return 0;
        }
        if (a[0] == "conflicts" && a.Length >= 2)
        {
            string db = Opt(a, "--db", config.Database);
            using var d = new Database(db);
            if (!d.PlanExists(a[1]))
            {
                return Fail($"unknown plan '{a[1]}'");
            }

            var bad = d.ListPlanOperations(a[1], onlyProblems: true);
            if (bad.Count == 0)
            {
                Console.WriteLine($"plan {a[1]}: no conflicts or failures");
                return 0;
            }
            Console.WriteLine($"plan {a[1]}: {bad.Count} problem(s)");
            foreach (var o in bad)
            {
                Console.WriteLine(
                    $"  {o.Sequence:D4} {o.Status, -8} {o.Type, -7} {o.SourceRoot}:{o.SourcePath} -> {o.DestRoot}:{o.DestPath} : {o.Error}"
                );
            }

            return 3;
        }
        if (a[0] == "import" && a.Length >= 2)
        {
            // UI-generated plan JSON -> DB, so the same executor can run it.
            string db = Opt(a, "--db", config.Database);
            string targetPath = Opt(a, "--target-path", "");
            var doc = PlanStaging.ImportJson(a[1]);
            using var d = new Database(db);
            string rootPath = targetPath;
            if (string.IsNullOrEmpty(rootPath))
            {
                rootPath = d.GetRoot(doc.TargetRoot)?.Path ?? doc.TargetPath;
            }

            PlanStaging.WriteToDatabase(d, doc, doc.TargetRoot, rootPath);
            Console.WriteLine($"imported plan {doc.PlanId} ({doc.Operations.Count} ops) into {db}");
            return 0;
        }
        string sourceDbPath = Opt(a, "--source-db", "");
        string sourceRoot = Opt(a, "--source-root", "");
        string targetDbPath = Opt(a, "--target-db", "");
        string targetRoot = Opt(a, "--target-root", "");
        if (
            string.IsNullOrEmpty(sourceDbPath)
            || string.IsNullOrEmpty(sourceRoot)
            || string.IsNullOrEmpty(targetDbPath)
            || string.IsNullOrEmpty(targetRoot)
        )
        {
            return Fail(
                "plan requires --source-db S.db --source-root R --target-db T.db --target-root R"
            );
        }

        string planId = Opt(a, "--plan", DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmss"));
        using var td = new Database(targetDbPath);
        using var sd = Paths.PathEquals(sourceDbPath, targetDbPath)
            ? null
            : Database.OpenReadOnly(sourceDbPath);
        var result = new Planner(td, Opt(a, "--hash-algo", config.HashAlgorithm)).PlanFromRoots(
            sd ?? td,
            sourceRoot,
            targetRoot,
            planId
        );
        PrintPlan(result);
        return 0;
    }

    private static void PrintPlan(Planner.PlanResult r)
    {
        Console.WriteLine($"Plan {r.PlanId}");
        Console.WriteLine(
            $"KEEP {r.Keep}  MOVE {r.Move}  COPY {r.Copy}  TRASH {r.Trash}  MKDIR {r.Mkdir}  SKIP_LINK {r.SkippedLinks}"
        );
        Console.WriteLine($"Bytes requiring actual copying: {r.BytesToCopy}");
        Console.WriteLine($"Bytes avoided through moves: {r.BytesAvoided}");
        Console.WriteLine("No files were modified.");
    }

    private static int Execute(string[] a)
    {
        if (a.Length == 0)
        {
            return Fail("execute <plan-id>");
        }

        string db = Opt(a, "--db", LoadConfig(a).Database);
        string? sourcePath = Has(a, "--source-path") ? Opt(a, "--source-path", "") : null;
        string? targetPath = Has(a, "--target-path") ? Opt(a, "--target-path", "") : null;
        using var d = new Database(db);
        if (!d.PlanExists(a[0]))
        {
            return Fail($"unknown plan '{a[0]}'");
        }

        if (!Has(a, "--yes"))
        {
            var (counts, est) = PlanSummary(d, a[0]);
            int total = counts.Values.Sum();
            Console.WriteLine(
                $"Plan {a[0]}: "
                    + string.Join(
                        "  ",
                        counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")
                    )
                    + $"  estBytes={est}"
            );
            Console.Write($"Execute {total} operations ({est} bytes to copy)? [y/N] ");
            string? ans = Console.ReadLine();
            if (
                !string.Equals(ans?.Trim(), "y", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(ans?.Trim(), "yes", StringComparison.OrdinalIgnoreCase)
            )
            {
                Console.WriteLine(
                    "Aborted (nothing was modified). Use --yes to approve non-interactively."
                );
                return 2;
            }
        }
        var sum = new Executor(d).Execute(
            a[0],
            sourcePath,
            targetPath,
            Has(a, "--resume"),
            Has(a, "--stop-on-error")
        );
        Console.WriteLine(
            $"execute {a[0]}: completed={sum.Completed} skipped={sum.Skipped} failed={sum.Failed} conflicts={sum.Conflicts}"
        );
        return sum.Failed == 0 && sum.Conflicts == 0 ? 0 : 3;
    }

    private static (Dictionary<string, int> Counts, long EstBytes) PlanSummary(
        Database db,
        string planId
    )
    {
        var counts = db.GetOperationCounts(planId);
        long est = db.GetPlan(planId)?.EstimatedBytesCopied ?? 0L;
        return (counts, est);
    }

    private static int Verify(string[] a)
    {
        if (a.Length == 0)
        {
            return Fail("verify <plan-id>");
        }

        string db = Opt(a, "--db", LoadConfig(a).Database);
        string? sourcePath = Has(a, "--source-path") ? Opt(a, "--source-path", "") : null;
        string? targetPath = Has(a, "--target-path") ? Opt(a, "--target-path", "") : null;
        using var database = Database.OpenReadOnly(db, pooling: false);
        if (!database.PlanExists(a[0]))
        {
            return Fail($"unknown plan '{a[0]}'");
        }

        var result = PlanVerifier.Verify(database, a[0], sourcePath, targetPath);
        foreach (var issue in result.Issues)
        {
            Console.WriteLine($"{issue.Path}: {issue.Message}");
        }

        Console.WriteLine(
            $"verify {a[0]}: ok={result.Ok} bad={result.Bad} skipped={result.Skipped}"
        );
        return result.Bad == 0 ? 0 : 3;
    }

    private static int Purge(string[] a)
    {
        string older = Opt(a, "--older-than", "");
        if (string.IsNullOrEmpty(older) || !Has(a, "--yes"))
        {
            return Fail(
                "purge --older-than 30d --yes [--path ROOT] (permanent delete, never implicit §16)"
            );
        }

        int days = int.TryParse(new string(older.TakeWhile(char.IsDigit).ToArray()), out var dd)
            ? dd
            : 30;
        string? basePath = Has(a, "--path") ? Opt(a, "--path", "") : null;
        string db = Opt(a, "--db", LoadConfig(a).Database);
        List<string> rootsToClean = new();
        if (!string.IsNullOrEmpty(basePath))
        {
            rootsToClean.Add(basePath);
        }
        else
        {
            using var d = new Database(db);
            rootsToClean.AddRange(d.ListRoots().Select(rr => rr.Path));
        }
        int deleted = 0;
        foreach (var rp in rootsToClean)
        {
            string trashBase = Path.Combine(rp, LoadConfig(a).TrashDirectoryName);
            if (!Directory.Exists(trashBase))
            {
                continue;
            }

            foreach (var dir in Directory.GetDirectories(trashBase))
            {
                var di = new DirectoryInfo(dir);
                if (DateTime.UtcNow - di.CreationTimeUtc > TimeSpan.FromDays(days))
                {
                    Directory.Delete(dir, true);
                    deleted++;
                    Log.Info($"purged {dir}");
                }
            }
        }
        Console.WriteLine($"purged {deleted} trash dirs older than {days}d");
        return 0;
    }

    private static int Diff(string[] a)
    {
        string sourceDb = Opt(a, "--source-db", ""),
            sourceRoot = Opt(a, "--source-root", "");
        string targetDb = Opt(a, "--target-db", ""),
            targetRoot = Opt(a, "--target-root", "");
        if (
            string.IsNullOrEmpty(sourceDb)
            || string.IsNullOrEmpty(sourceRoot)
            || string.IsNullOrEmpty(targetDb)
            || string.IsNullOrEmpty(targetRoot)
        )
        {
            return Fail(
                "diff requires --source-db S.db --source-root R --target-db T.db --target-root R"
            );
        }

        var s = Inventory.Diff(
            sourceDb,
            sourceRoot,
            targetDb,
            targetRoot,
            Opt(a, "--hash-algo", LoadConfig(a).HashAlgorithm)
        );
        Console.WriteLine(
            $"source-only: {s.SourceOnly}  target-only: {s.TargetOnly}  changed: {s.Changed}  identical: {s.Identical}  unverified: {s.Unverified}"
        );
        Console.WriteLine($"skipped links: {s.SkippedLinks}  link conflicts: {s.LinkConflicts}");
        Console.WriteLine(
            $"scan status: source={s.SourceScanStatus ?? "never scanned"} target={s.TargetScanStatus ?? "never scanned"}"
        );
        foreach (var l in s.Samples)
        {
            Console.WriteLine("  " + l);
        }

        return 0;
    }

    private static int DbTest(string[] a)
    {
        // §39 db-test: create db, table, insert, read, txn commit/rollback, delete temp db
        string tmp = Path.Combine(
            Path.GetTempPath(),
            "bn-dbtest-" + Guid.NewGuid().ToString("N") + ".db"
        );
        try
        {
            using var d = new Database(tmp);
            d.UpsertRoot(
                new StorageRootRow(
                    "t",
                    "t",
                    Path.GetTempPath(),
                    true,
                    "test",
                    "unknown",
                    Database.UtcNow()
                )
            );
            long sid = d.BeginScan("t");
            d.UpsertFileEntry(
                new FileEntryRow(
                    0,
                    "t",
                    "a.txt",
                    "a.txt",
                    3,
                    Database.UtcNow(),
                    null,
                    null,
                    sid,
                    FileStatus.Ok,
                    null
                )
            );
            var got = d.ListFiles("t");
            if (got.Count != 1)
            {
                throw new Exception("readback failed");
            }

            using var tx = d.BeginTransaction();
            d.UpsertFileEntry(
                new FileEntryRow(
                    0,
                    "t",
                    "b.txt",
                    "b.txt",
                    1,
                    Database.UtcNow(),
                    null,
                    null,
                    sid,
                    FileStatus.Ok,
                    null
                )
            );
            tx.Rollback();
            if (d.ListFiles("t").Count != 1)
            {
                throw new Exception("rollback failed");
            }

            d.FinishScan(sid, ScanStatus.Completed);
            Console.WriteLine("db-test: PASS (create/insert/read/commit/rollback ok)");
            return 0;
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch { }
        }
    }

    private static int ScanTest(string[] a)
    {
        if (a.Length == 0)
        {
            return Fail("scan-test <path>");
        }

        string p = a[0];
        string tmp = Path.Combine(
            Path.GetTempPath(),
            "bn-scantest-" + Guid.NewGuid().ToString("N") + ".db"
        );
        try
        {
            using var d = new Database(tmp);
            d.UpsertRoot(
                new StorageRootRow(
                    "s",
                    "s",
                    Path.GetFullPath(p),
                    false,
                    Paths.GetFileSystemId(p),
                    "unknown",
                    Database.UtcNow()
                )
            );
            var sc = new Scanner(d);
            var (s, e) = sc.ScanRoot("s");
            Console.WriteLine($"scan-test: PASS ({s} files, {e} errors) on {p}");
            Console.WriteLine(
                $"  uname -m equivalent: {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}"
            );
            return 0;
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch { }
        }
    }
}
