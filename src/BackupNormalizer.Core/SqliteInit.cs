using Microsoft.Data.Sqlite;

namespace BackupNormalizer;

/// <summary>
/// SQLite provider selection (spec §3.2). Desktop default is the bundled
/// e_sqlite3 native binary; the QNAP/Alpine ARM32 target prefers the system
/// libsqlite3 (set BN_SQLITE=system, see Dockerfile.qnap). "auto" probes the
/// bundle and falls back to system instead of failing at first query.
/// </summary>
public static class SqliteInit
{
    private static bool _done;
    private static readonly object Gate = new();

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_done) return;
            string mode = (Environment.GetEnvironmentVariable("BN_SQLITE") ?? "auto").Trim().ToLowerInvariant();
            if (mode == "system")
            {
                UseSystem();
            }
            else
            {
                try
                {
                    SQLitePCL.Batteries.Init();
                    // Native load is lazy: prove the bundled lib actually opens.
                    using var probe = new SqliteConnection("Data Source=:memory:");
                    probe.Open();
                    probe.Close();
                }
                catch
                {
                    try { UseSystem(); } catch { /* loud failure surfaces at first real open */ }
                }
            }
            _done = true;
        }
    }

    private static void UseSystem()
    {
        // Alpine (and most distros) ship only the versioned soname
        // (libsqlite3.so.0); the provider probes unversioned names.
        System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(
            typeof(SQLitePCL.SQLite3Provider_sqlite3).Assembly,
            (name, asm, path) =>
            {
                if (name is "sqlite3" or "libsqlite3" or "libsqlite3.so"
                    && System.Runtime.InteropServices.NativeLibrary.TryLoad(
                        "libsqlite3.so.0", asm, null, out nint handle))
                    return handle;
                return IntPtr.Zero; // fall through to default probing
            });
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
    }
}
