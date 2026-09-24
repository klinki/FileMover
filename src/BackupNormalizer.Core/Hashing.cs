using System.Security.Cryptography;

namespace BackupNormalizer;

public interface IContentHasher
{
    string AlgorithmName { get; }
    string HashFile(string absPath, long expectedSize);
    string HashStream(Stream s);
}

public sealed class Sha256Hasher : IContentHasher
{
    public string AlgorithmName => "sha256";
    private const int BufSize = 4 * 1024 * 1024; // §30: 1-8 MiB

    public string HashFile(string absPath, long expectedSize)
    {
        using var fs = new FileStream(absPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufSize, FileOptions.SequentialScan);
        return HashStream(fs);
    }

    public string HashStream(Stream s)
    {
        using var sha = SHA256.Create();
        var buf = new byte[BufSize];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
            sha.TransformBlock(buf, 0, n, null, 0);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }
}

/// <summary>
/// Spec §3.3: preferred BLAKE3, fallback SHA-256 acceptable. The abstraction
/// allows swapping algorithms later. This build defaults to SHA-256 (managed,
/// ARM32-safe, no native dep) and maps "blake3" requests to SHA-256 with a
/// recorded algorithm name so stored rows remain explicit.
/// </summary>
public static class HasherFactory
{
    public static IContentHasher Create(string? name)
    {
        name = (name ?? "sha256").ToLowerInvariant();
        if (name is "blake3" or "blake3-managed")
        {
            // TODO: plug Blake3.Net / Blake3 native when ARM32/QNAP validated.
            // For MVP we use SHA-256 bytes but label rows with requested algorithm
            // only if caller explicitly wants strict naming. We keep "sha256" to
            // avoid claiming BLAKE3 identity falsely (Invariant 2/3 safety).
            Console.Error.WriteLine("warning: blake3 requested but this build uses sha256 fallback (spec §3.3 acceptable)");
        }
        return new Sha256Hasher();
    }

    public static string NormalizeAlgorithm(string? name)
    {
        name = (name ?? "sha256").ToLowerInvariant();
        return name == "blake3" ? "sha256" : name; // fallback mapping
    }

    /// <summary>Stage 2 (§9.1): fast rejection fingerprint from head/middle/tail 1MiB. Never definitive.</summary>
    public static string? PartialFingerprint(string absPath, long size)
    {
        try
        {
            const int Block = 1 * 1024 * 1024;
            using var sha = SHA256.Create();
            using var fs = new FileStream(absPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            var buf = new byte[Block];
            void Feed(long offset, int len)
            {
                fs.Seek(offset, SeekOrigin.Begin);
                int total = 0;
                while (total < len)
                {
                    int n = fs.Read(buf, total, len - total);
                    if (n == 0) break;
                    total += n;
                }
                sha.TransformBlock(buf, 0, total, null, 0);
            }
            if (size <= 3L * Block)
            {
                fs.Seek(0, SeekOrigin.Begin);
                int n; while ((n = fs.Read(buf, 0, buf.Length)) > 0) sha.TransformBlock(buf, 0, n, null, 0);
            }
            else
            {
                Feed(0, Block);
                Feed(size / 2 - Block / 2, Block);
                Feed(size - Block, Block);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return "partial-sha256:" + Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
        catch { return null; }
    }
}
