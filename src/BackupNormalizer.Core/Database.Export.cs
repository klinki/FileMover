using Microsoft.Data.Sqlite;

namespace BackupNormalizer;

public sealed partial class Database
{
    /// <summary>Export committed SQLite contents to one independently usable database without migrating the source.</summary>
    public static void ExportSnapshot(string sourcePath, string destinationPath)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        destinationPath = Path.GetFullPath(destinationPath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Source database file not found.", sourcePath);
        foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
            if (Paths.PathEquals(destinationPath, sourcePath + suffix))
                throw new ArgumentException("The export destination cannot be the source database or one of its SQLite companions.");
        foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
            if (File.Exists(destinationPath + suffix) || Directory.Exists(destinationPath + suffix))
                throw new IOException($"Export destination or SQLite companion already exists: {destinationPath + suffix}");
        string directory = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".bn-export-" + Guid.NewGuid().ToString("N") + ".db");
        bool ownsTemporary = false;
        try
        {
            using (var reservation = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                ownsTemporary = true;
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString()))
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temporary, Mode = SqliteOpenMode.ReadWrite, Pooling = false
            }.ToString()))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
                using var check = destination.CreateCommand();
                check.CommandText = "PRAGMA integrity_check;";
                if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
                    throw new IOException("The exported database failed SQLite integrity checking.");
                check.CommandText = "PRAGMA foreign_key_check;";
                using (var reader = check.ExecuteReader())
                    if (reader.Read()) throw new IOException("The exported database contains a foreign-key violation.");
                // The portable artifact must not depend on separate WAL/SHM files.
                check.CommandText = "PRAGMA journal_mode=DELETE;";
                if (!string.Equals(check.ExecuteScalar() as string, "delete", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Cannot finalize the exported database as a single file.");
            }
            File.Move(temporary, destinationPath); // Refuse a destination created by another process during export.
        }
        finally
        {
            if (ownsTemporary)
                foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
                    if (File.Exists(temporary + suffix)) File.Delete(temporary + suffix);
        }
    }
}
