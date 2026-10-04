using System.Security.Cryptography;
using System.Text;
using BackupNormalizer;

namespace BackupNormalizer.Tests;

internal sealed class LocationChangesFixture : IDisposable
{
    public string DirectoryPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "bn-locations-" + Guid.NewGuid().ToString("N"));
    public const string Modified = "2026-01-01T00:00:00Z";

    public LocationChangesFixture() => Directory.CreateDirectory(DirectoryPath);

    public string PathFor(string name) => Path.Combine(DirectoryPath, name + ".db");

    public static string Digest(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    public string Seed(
        string name,
        string[] paths,
        string root = "r",
        string content = "same",
        bool hashed = true,
        string caseSensitivity = "sensitive",
        string status = ScanStatus.Completed
    )
    {
        string path = PathFor(name);
        using var db = Database.OpenWritable(path, pooling: false);
        db.UpsertRoot(
            new StorageRootRow(
                root,
                root,
                "/offline/same-root",
                false,
                "device",
                caseSensitivity,
                Modified
            )
        );
        long scan = db.BeginScan(root);
        foreach (string relative in paths)
        {
            long id = db.UpsertFileEntry(
                new FileEntryRow(
                    0,
                    root,
                    relative,
                    relative.Split('/').Last(),
                    Encoding.UTF8.GetByteCount(content),
                    Modified,
                    null,
                    null,
                    scan,
                    FileStatus.Ok,
                    null
                )
            );
            if (hashed)
                db.UpsertHash(
                    new FileHashRow(
                        id,
                        "sha256",
                        Digest(content),
                        Encoding.UTF8.GetByteCount(content),
                        Modified,
                        Modified,
                        HashState.Ok
                    )
                );
        }
        db.FinishScan(scan, status);
        return path;
    }

    public LocationChangesReport Analyze(
        string a = "a",
        string b = "b",
        string rootA = "r",
        string rootB = "r"
    ) => FileLocationChanges.Analyze(new(PathFor(a), rootA), new(PathFor(b), rootB));

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
