"""Prepare portable synthetic replay plans through the ordinary PC application."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys

output = Path(sys.argv[1]).resolve()
cli = output / "source/src/BackupNormalizer/bin/Release/net10.0/BackupNormalizer.dll"
fixture = output / "execution-fixture"
fixture.mkdir(exist_ok=False)
target, source = fixture / "target", fixture / "source"
target.mkdir()
source.mkdir()
files = {
    "target/old/video.mp4": b"video from Vienna\n",
    "target/duplicate.zip": b"gallery from Vienna\n",
    "target/keep.txt": b"keep\n",
    "target/indexed-trash.zip": b"indexed survivor\n",
    "target/indexed-survivor.zip": b"indexed survivor\n",
    "target/conflict-source.txt": b"new content\n",
    "target/conflict-dest.txt": b"existing content\n",
    "target/after-conflict.txt": b"leave in place\n",
    "target/hash-source.txt": b"tampered",
    "target/unsafe-trash.zip": b"only copy\n",
    "target/blocker": b"regular file\n",
    "target/link-source.txt": b"linked content\n",
    "source/remote.zip": b"external copy\n",
    "source/recovery.bin": b"recover moved file\n",
}
for name, content in files.items():
    path = fixture / name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(content)

db = fixture / "pc.db"
log = []


def run(*args):
    result = subprocess.run(["dotnet", str(cli), *map(str, args)], cwd=fixture,
                            capture_output=True, text=True)
    log.extend(["COMMAND: " + " ".join(map(str, args)), result.stdout, result.stderr])
    (output / "execution-fixture.log").write_text("\n".join(log))
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)


def op(kind, src=None, dst=None, content=None, scope="Target", reason=None):
    return {"type": kind, "sourceKind": scope if src else None,
            "sourceRoot": ("source" if scope == "Source" else "target") if src else None,
            "sourcePath": src, "destinationRoot": "target" if dst else None,
            "destinationPath": dst, "expectedSize": len(content) if content is not None else 0,
            "expectedHash": hashlib.sha256(content).hexdigest() if content is not None else None,
            "skipReason": reason}


plans = {
    "main": [op("MKDIR", dst="album"),
             op("MOVE", "old/video.mp4", "album/video-from-vienna-\u010d.mp4", files["target/old/video.mp4"]),
             op("COPY", "duplicate.zip", "album/gallery-us-in-vienna.zip", files["target/duplicate.zip"]),
             op("COPY", "remote.zip", "album/from-source.zip", files["source/remote.zip"], "Source"),
             op("KEEP", dst="keep.txt", content=files["target/keep.txt"]),
             op("VERIFY", dst="album/video-from-vienna-\u010d.mp4", content=files["target/old/video.mp4"]),
             op("TRASH", "duplicate.zip", content=files["target/duplicate.zip"]),
             op("TRASH", "indexed-trash.zip", content=files["target/indexed-trash.zip"]),
             op("SKIP_LINK", "not-a-content-file", reason="PC excluded link")],
    "conflict": [op("COPY", "conflict-source.txt", "conflict-dest.txt", files["target/conflict-source.txt"]),
                 op("MOVE", "after-conflict.txt", "must-not-move.txt", files["target/after-conflict.txt"])],
    "hash-conflict": [op("MOVE", "hash-source.txt", "hash-dest.txt", b"original")],
    "unsafe-trash": [op("TRASH", "unsafe-trash.zip", content=files["target/unsafe-trash.zip"])],
    "failure": [op("MKDIR", dst="blocker/child")],
    "link": [op("MOVE", "link.txt", "link-dest.txt", files["target/link-source.txt"])],
    "recovery": [op("MOVE", "recovery-source.mp4", "recovery-dest.mp4", files["source/recovery.bin"])],
}
run("root", "add", "target", target, "--db", db)
run("scan", "target", "--db", db, "--mft", "off", "--usn", "off", "--no-progress")
run("hash", "target", "--db", db, "--no-progress")
for plan_id, operations in plans.items():
    for sequence, operation in enumerate(operations, 1):
        operation["id"] = sequence
    document = {"planId": plan_id, "createdUtc": "2026-10-05T00:00:00Z",
                "estimatedBytesCopied": sum(o["expectedSize"] for o in operations if o["type"] == "COPY"),
                "sourceDatabasePath": str(db), "sourceRoot": "source", "sourcePath": str(source),
                "targetRoot": "target", "targetPath": str(target), "operations": operations}
    path = fixture / (plan_id + ".json")
    path.write_text(json.dumps(document, ensure_ascii=False, indent=2))
    run("plan", "import", path, "--db", db)
run("db", "export", "--db", db, "--output", fixture / "plans.db")
(fixture / "manifest.json").write_text(json.dumps({"plans": list(plans), "fileSha256": {
    name: hashlib.sha256(content).hexdigest() for name, content in files.items()}}, indent=2) + "\n")
print("PC-prepared portable execution fixture:", fixture / "plans.db")
