"""Check generated EF query execution and persisted values before ARM publication."""
import hashlib
import json
from pathlib import Path
import shutil
import sqlite3
import subprocess
import sys

output = Path(sys.argv[1]).resolve()
cli = output / "source/src/BackupNormalizer/bin/Release/net10.0/BackupNormalizer.dll"
helper_dir = output / "source/src/BackupNormalizer.Migrations/bin/Release/net10.0"
for name in ("BackupNormalizer.Migrations", "BackupNormalizer.Migrations.deps.json", "BackupNormalizer.Migrations.runtimeconfig.json", "BackupNormalizer.Migrations.dll"):
    shutil.copyfile(helper_dir / name, cli.parent / name)
work = output / "managed-acceptance"
work.mkdir(exist_ok=False)
data = work / "data"
data.mkdir()
(data / "cache").mkdir()
(data / "first.txt").write_bytes(b"first\n")
(data / "second.txt").write_bytes(b"second\n")
(data / "cache/ignored.txt").write_bytes(b"ignored\n")
other = work / "other"
other.mkdir()
(other / "other.txt").write_bytes(b"other\n")
db_path = data / "inventory.db"
log = []


def run(*arguments, codes=(0,)):
    result = subprocess.run(["dotnet", str(cli), *map(str, arguments)], cwd=work, capture_output=True, text=True)
    log.extend(["COMMAND: " + " ".join(map(str, arguments)), result.stdout, result.stderr])
    (output / "precompiled-managed.log").write_text("\n".join(log))
    if result.returncode not in codes:
        raise RuntimeError(result.stdout + result.stderr)
    return result.stdout


run("root", "add", "r", data, "--db", db_path)
run("root", "add", "r", data, "--name", "updated", "--db", db_path)
run("root", "add", "other", other, "--db", db_path)
run("root", "list", "--db", db_path)
run("scan", "r", "--db", db_path, "--mft", "off", "--usn", "off", "--no-progress", "--exclude-path-regex", "^cache/")
run("scan", "other", "--db", db_path, "--mft", "off", "--usn", "off", "--no-progress")
run("scan", "errors", "r", "--scan", "1", "--db", db_path, "--json")
run("hash", "r", "--db", db_path, "--no-progress")
statuses = json.loads(run("status", "r", "--db", db_path, "--json"))
assert statuses[0]["regularFiles"] == 2 and statuses[0]["usableHashes"] == 2
assert statuses[0]["latestScan"]["scannedCount"] == 2
assert statuses[0]["latestScan"]["errorCount"] == 0
assert statuses[0]["latestScan"]["mode"] == "Recursive"
assert "0 hashed" in run("hash", "r", "--db", db_path, "--no-progress")
assert "1 hashed" in run("hash", "--needed", "--db", db_path, "--no-progress")
(data / "first.txt").write_bytes(b"changed content\n")
(data / "second.txt").rename(data / "moved.txt")
run("scan", "r", "--db", db_path, "--mft", "off", "--usn", "off", "--no-progress")
run("hash", "--needed", "--db", db_path, "--no-progress")
export = work / "export.db"
run("db", "export", "--db", db_path, "--output", export, "--json")
with sqlite3.connect(f"file:{export.as_posix()}?mode=ro", uri=True) as db:
    assert db.execute("SELECT Name, Path FROM StorageRoot WHERE Id='r'").fetchone() == ("updated", str(data))
    assert db.execute("SELECT COUNT(*) FROM __EFMigrationsHistory").fetchone()[0] == 6
    assert db.execute("SELECT Status FROM FileEntry WHERE RelativePath='second.txt'").fetchone()[0] == "Missing"
    assert db.execute("SELECT COUNT(*) FROM FileEntry WHERE RelativePath LIKE 'cache/%'").fetchone()[0] == 0
    for name, content in {"first.txt": b"changed content\n", "moved.txt": b"second\n"}.items():
        row = db.execute("SELECT e.Name, e.Size, h.SizeAtHash, h.Digest, e.ModifiedUtc=h.ModifiedUtcAtHash "
                         "FROM FileEntry e JOIN FileHash h ON h.FileEntryId=e.Id WHERE e.RelativePath=?", (name,)).fetchone()
        assert row == (name, len(content), len(content), hashlib.sha256(content).hexdigest(), 1), row
    assert db.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
print("Precompiled application queries: roots, per-root filtering, scans, hashes, updates, reuse and export passed")
