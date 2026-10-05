"""Run the actual CLI on synthetic QNAP data and retain a portable inventory."""
import argparse
import base64
from datetime import datetime, timezone
import hashlib
import io
import json
from pathlib import Path
import shlex
import sqlite3
import struct
import subprocess
import tarfile

parser = argparse.ArgumentParser()
parser.add_argument("output", type=Path)
parser.add_argument("--host", required=True)
parser.add_argument("--base-image", default="backup-normalizer:qnap-arm32")
parser.add_argument("--legacy-fixture", type=Path, required=True)
args = parser.parse_args()
repo = Path(__file__).resolve().parents[1]
output = args.output.resolve()
if not output.is_relative_to(repo / "publish"):
    raise ValueError("Output must be under the repository publish directory.")
if (output / "nas-result.json").exists():
    raise FileExistsError("Use a new output directory to preserve previous evidence.")


def docker(arguments, data=None, check=True):
    remote = shlex.join(["/share/CACHEDEV1_DATA/.qpkg/container-station/bin/docker", *arguments])
    result = subprocess.run(["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                             "admin@" + args.host, remote], input=data, capture_output=True)
    if check and result.returncode:
        raise RuntimeError((result.stdout + result.stderr).decode("utf-8", errors="replace"))
    return result


files = {name: output / "app" / name for name in
         ("BackupNormalizer", "BackupNormalizer.Migrations", "libe_sqlite3.so")}
files.update({name: repo / name for name in ("qnap-check.sh", "qnap-inventory.sh")})
files["inventory-acceptance.sh"] = repo / "deploy/inventory-aot-acceptance.sh"
files["legacy.db"] = args.legacy_fixture.resolve()
record = {"baseImage": args.base_image, "files": {}}
for name, path in files.items():
    binary = path.read_bytes()
    entry = {"sha256": hashlib.sha256(binary).hexdigest(), "size": len(binary)}
    if binary[:4] == b"\x7fELF":
        assert binary[4] == 1 and struct.unpack_from("<H", binary, 18)[0] == 40
        offset = struct.unpack_from("<I", binary, 28)[0]
        size, count = struct.unpack_from("<HH", binary, 42)
        loads = [struct.unpack_from("<8I", binary, offset + index * size) for index in range(count)]
        loads = [values for values in loads if values[0] == 1]
        assert loads and all(values[7] >= 32768 and (values[1] - values[2]) % 32768 == 0 for values in loads)
        entry["loadAlignment"] = min(values[7] for values in loads)
    record["files"][name] = entry

original = docker(["ps", "--format", "{{.Names}}"]).stdout.decode().splitlines()
stamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
prefix = "backup-normalizer-inventory-aot-" + stamp
builder = prefix + "-builder"
image = "backup-normalizer:inventory-aot-" + stamp
docker(["create", "--name", builder, "--network", "none", "--entrypoint", "/bin/sh", args.base_image, "-c", "true"])
buffer = io.BytesIO()
with tarfile.open(fileobj=buffer, mode="w") as archive:
    for name, path in files.items():
        info = archive.gettarinfo(str(path), arcname=name)
        info.mode = 0o755 if name.startswith("BackupNormalizer") or name.endswith(".sh") else 0o644
        with path.open("rb") as source:
            archive.addfile(info, source)
    for name, content in {"hello.txt": b"hello\n", "remove.txt": b"removed\n",
                          "album/video.mp4": b"video fixture\n", "cache/ignored.txt": b"excluded\n",
                          "photo-vienna-\u010d.txt": "Unicode fixture\n".encode(), "empty.txt": b""}.items():
        info = tarfile.TarInfo("inventory-fixture/" + name)
        info.size = len(content)
        info.mode = 0o644
        archive.addfile(info, io.BytesIO(content))
docker(["cp", "-", builder + ":/app"], buffer.getvalue())
docker(["commit", "--change", 'ENTRYPOINT ["/app/BackupNormalizer"]',
        "--change", 'CMD ["--help"]', builder, image])
record["image"] = image
container = prefix + "-acceptance"
result = docker(["run", "--name", container, "--network", "none", "--read-only",
                 "--tmpfs", "/tmp:rw,size=256m", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
                 "--entrypoint", "/bin/sh", image, "/app/inventory-acceptance.sh"], check=False)
stdout = result.stdout.decode("utf-8", errors="replace")
stderr = result.stderr.decode("utf-8", errors="replace")
inspection = json.loads(docker(["inspect", container]).stdout)[0]
assert not inspection["Mounts"]
record.update({"container": container, "exitCode": result.returncode,
               "oomKilled": inspection["State"]["OOMKilled"], "mounts": inspection["Mounts"]})
if "EXPORT_BASE64_BEGIN\n" in stdout:
    encoded = stdout.split("EXPORT_BASE64_BEGIN\n", 1)[1].split("EXPORT_BASE64_END", 1)[0]
    (output / "nas-export.db").write_bytes(base64.b64decode(encoded))
    stdout = stdout.replace(encoded, "[portable database saved locally]\n")
    with sqlite3.connect(f"file:{(output / 'nas-export.db').as_posix()}?mode=ro", uri=True) as db:
        assert db.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
        assert db.execute("PRAGMA foreign_key_check").fetchall() == []
        assert db.execute("SELECT COUNT(*) FROM __EFMigrationsHistory").fetchone()[0] == 6
        root = db.execute("SELECT Name, Path FROM StorageRoot WHERE Id='nas'").fetchone()
        assert root == ("updated", "/tmp/bn-inventory/data"), root
        paths = db.execute("SELECT RelativePath, Status, EntryKind FROM FileEntry").fetchall()
        assert ("remove.txt", "Missing", "File") in paths
        assert ("album/video.mp4", "Missing", "File") in paths
        assert ("video-moved.mp4", "Ok", "File") in paths
        assert any(row[0] == "photo-vienna-\u010d.txt" and row[1] == "Ok" for row in paths)
        assert not any(row[0].startswith("cache/") for row in paths)
        assert db.execute("SELECT COUNT(*) FROM FileHash WHERE State='Ok'").fetchone()[0] >= 4
        expected = {"hello.txt": b"modified content\n", "video-moved.mp4": b"video fixture\n",
                    "photo-vienna-\u010d.txt": b"Unicode fixture\n", "empty.txt": b""}
        for name, content in expected.items():
            row = db.execute("SELECT e.Name, e.Size, h.SizeAtHash, h.Digest, e.ModifiedUtc=h.ModifiedUtcAtHash "
                             "FROM FileEntry e JOIN FileHash h ON e.Id=h.FileEntryId "
                             "WHERE e.RelativePath=? AND h.Algorithm='sha256'", (name,)).fetchone()
            assert row == (name, len(content), len(content), hashlib.sha256(content).hexdigest(), 1), (name, row)
        record["exportVerified"] = True
record["stdout"] = stdout
record["stderr"] = stderr
(output / "inventory-nas.log").write_text(stdout + stderr, encoding="utf-8")
integrity = docker(["run", "--name", prefix + "-integrity", "--network", "none", "--read-only",
                    "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
                    "--entrypoint", "/usr/bin/sha256sum", image, *["/app/" + name for name in files]])
actual = {line.split()[1].removeprefix("/app/"): line.split()[0] for line in integrity.stdout.decode().splitlines()}
record["hashesMatch"] = all(actual.get(name) == entry["sha256"] for name, entry in record["files"].items())
record["originalRunningServices"] = original
after = docker(["ps", "--format", "{{.Names}}"]).stdout.decode().splitlines()
record["allOriginalServicesStillRunning"] = set(original).issubset(after)
(output / "nas-result.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
print(stdout + stderr)
print("Image:", image)
assert record["hashesMatch"] and record["allOriginalServicesStillRunning"]
raise SystemExit(0 if result.returncode == 0 and record.get("exportVerified") else 1)
