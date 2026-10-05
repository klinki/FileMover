using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace BackupNormalizer;

public static class DatabaseSchema
{
    public static void EnsureCurrent(string path)
    {
        var manifest = MigrationPackage.Manifest;
        if (File.Exists(path))
        {
            using var connection = Open(path, SqliteOpenMode.ReadOnly);
            if (ReadHistory(connection, manifest).Count == manifest.Steps.Length)
                return;
        }
        string helper = Path.Combine(
            AppContext.BaseDirectory,
            "BackupNormalizer.Migrations" + (OperatingSystem.IsWindows() ? ".exe" : "")
        );
        if (!File.Exists(helper))
            throw new FileNotFoundException(
                "Database creation or upgrade requires the matching AOT migration helper.",
                helper
            );
        var start = new ProcessStartInfo(helper)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--db");
        start.ArgumentList.Add(path);
        start.ArgumentList.Add("--manifest-hash");
        start.ArgumentList.Add(MigrationPackage.Hash);
        using var process =
            Process.Start(start) ?? throw new IOException("Cannot start migration helper.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new IOException(
                $"Migration helper failed: {error.GetAwaiter().GetResult().Trim()}"
            );
        if (!string.IsNullOrWhiteSpace(output.GetAwaiter().GetResult()))
            Console.Error.WriteLine(output.Result.Trim());
        using var check = Open(path, SqliteOpenMode.ReadOnly);
        if (ReadHistory(check, manifest).Count != manifest.Steps.Length)
            throw new IOException("Migration helper did not produce the required schema.");
    }

    internal static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = mode,
                Pooling = false,
                ForeignKeys = true,
                DefaultTimeout = 5,
            }.ToString()
        );
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal static List<string> ReadHistory(
        SqliteConnection connection,
        MigrationManifest manifest,
        SqliteTransaction? transaction = null
    )
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory';";
        if (Convert.ToInt64(command.ExecuteScalar()) == 0)
        {
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
            if (Convert.ToInt64(command.ExecuteScalar()) != 0)
                throw new InvalidOperationException(
                    "Database has tables without a supported EF migration history."
                );
            return [];
        }
        command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;";
        using var reader = command.ExecuteReader();
        var applied = new List<string>();
        while (reader.Read())
            applied.Add(reader.GetString(0));
        if (
            applied.Count > manifest.Steps.Length
            || !applied.SequenceEqual(manifest.Steps.Take(applied.Count).Select(s => s.Id))
        )
            throw new InvalidOperationException(
                "Database schema is newer, unknown, or has an incomplete migration history. No upgrade was attempted."
            );
        return applied;
    }

    public static string? Upgrade(string path, string expectedHash) =>
        Upgrade(path, MigrationPackage.Manifest, expectedHash, MigrationPackage.Hash);

    internal static string? Upgrade(
        string path,
        MigrationManifest manifest,
        string expectedHash,
        string actualHash
    )
    {
        if (expectedHash != actualHash)
            throw new InvalidOperationException(
                "Migration helper and application packages do not match."
            );
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // All helper instances cooperate on this persistent lock file; never unlink a held lock.
        using var migrationLock = new FileStream(
            path + ".bn-migration.lock",
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );
        using var connection = Open(path, SqliteOpenMode.ReadWriteCreate);
        var applied = ReadHistory(connection, manifest);
        if (applied.Count == manifest.Steps.Length)
            return null;

        string? backup = null;
        if (applied.Count > 0)
        {
            backup = path + ".before-migration-" + Guid.NewGuid().ToString("N") + ".db";
            using var reservation = new FileStream(
                backup,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None
            );
            reservation.Dispose();
            using var destination = Open(backup, SqliteOpenMode.ReadWrite);
            connection.BackupDatabase(destination);
            CheckIntegrity(destination);
        }
        try
        {
            foreach (var step in manifest.Steps.Skip(applied.Count))
            {
                // This package only admits transactional commands. The generator rejects
                // transaction-suppressed migrations rather than guessing about table rebuilds.
                using var transaction = connection.BeginTransaction();
                // Recheck after SQLite grants the write lock, including changes by
                // ordinary EF migration processes that do not use our helper lock.
                if (
                    ReadHistory(connection, manifest, transaction).Count
                    != Array.IndexOf(manifest.Steps, step)
                )
                    throw new IOException(
                        "Database schema changed during upgrade. Retry after the other writer finishes."
                    );
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (MigrationId TEXT NOT NULL CONSTRAINT PK___EFMigrationsHistory PRIMARY KEY, ProductVersion TEXT NOT NULL);";
                command.ExecuteNonQuery();
                foreach (string sql in step.Commands)
                {
                    command.CommandText = sql;
                    command.ExecuteNonQuery();
                }
                command.CommandText =
                    "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ($id, $version);";
                command.Parameters.AddWithValue("$id", step.Id);
                command.Parameters.AddWithValue("$version", step.ProductVersion);
                command.ExecuteNonQuery();
                CheckIntegrity(connection, transaction);
                transaction.Commit();
            }
            if (ReadHistory(connection, manifest).Count != manifest.Steps.Length)
                throw new IOException("Migration history does not match the required schema.");
            return backup;
        }
        catch (Exception ex)
        {
            throw new IOException(
                $"Schema upgrade failed. Backup: {backup ?? "new database; no previous inventory"}. {ex.Message}",
                ex
            );
        }
    }

    private static void CheckIntegrity(
        SqliteConnection connection,
        SqliteTransaction? transaction = null
    )
    {
        using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
            throw new IOException("Database failed SQLite integrity checking.");
        check.CommandText = "PRAGMA foreign_key_check;";
        using var reader = check.ExecuteReader();
        if (reader.Read())
            throw new IOException("Database contains a foreign-key violation.");
    }
}
