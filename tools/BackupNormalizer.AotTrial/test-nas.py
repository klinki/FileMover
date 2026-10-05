"""Run the trial in isolated QNAP containers through an existing SSH identity."""
import argparse
from datetime import datetime, timezone
import hashlib
import io
import json
from pathlib import Path
import shlex
import struct
import subprocess
import tarfile

parser = argparse.ArgumentParser()
parser.add_argument("output", type=Path)
parser.add_argument("--host", required=True)
parser.add_argument("--base-image", default="backup-normalizer:qnap-arm32")
args = parser.parse_args()
repo = Path(__file__).resolve().parents[2]
output = args.output.resolve()
if not output.is_relative_to(repo / "publish"):
    raise ValueError("Trial output must be under the repository's publish directory.")
if (output / "nas-result.json").exists():
    raise FileExistsError("Preserve earlier results; use a new trial output directory.")


def docker(arguments, data=None, check=True):
    remote = shlex.join(["/share/CACHEDEV1_DATA/.qpkg/container-station/bin/docker", *arguments])
    result = subprocess.run(["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                             "admin@" + args.host, remote], input=data, capture_output=True)
    if check and result.returncode:
        raise RuntimeError((result.stdout + result.stderr).decode("utf-8", errors="replace"))
    return result


files = {name: output / "app" / name for name in ("BackupNormalizer.AotTrial", "libe_sqlite3.so")}
files.update({"ef-trial-fixtures/" + name: output / "fixtures" / name for name in ("current.db", "legacy.db")})
record = {"baseImage": args.base_image, "files": {}, "cases": []}
for name, path in files.items():
    binary = path.read_bytes()
    entry = {"sha256": hashlib.sha256(binary).hexdigest(), "size": len(binary)}
    if binary[:4] == b"\x7fELF":
        assert binary[4] == 1 and struct.unpack_from("<H", binary, 18)[0] == 40
        offset = struct.unpack_from("<I", binary, 28)[0]
        size, count = struct.unpack_from("<HH", binary, 42)
        loads = []
        for index in range(count):
            values = struct.unpack_from("<8I", binary, offset + index * size)
            if values[0] == 1:
                loads.append({"offset": values[1], "address": values[2], "alignment": values[7]})
        assert loads and all(x["alignment"] >= 32768 and (x["offset"] - x["address"]) % 32768 == 0 for x in loads)
        entry["loadSegments"] = loads
    record["files"][name] = entry

original = docker(["ps", "--format", "{{.Names}}"]).stdout.decode().splitlines()
stamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
prefix = "backup-normalizer-ef-aot-trial-" + stamp
builder = prefix + "-builder"
image = "backup-normalizer:ef-aot-trial-" + stamp
docker(["create", "--name", builder, "--network", "none", "--entrypoint", "/bin/sh", args.base_image, "-c", "true"])
buffer = io.BytesIO()
with tarfile.open(fileobj=buffer, mode="w") as archive:
    for name, path in files.items():
        info = archive.gettarinfo(str(path), arcname=name)
        info.mode = 0o755 if name == "BackupNormalizer.AotTrial" else 0o644
        with path.open("rb") as source:
            archive.addfile(info, source)
docker(["cp", "-", builder + ":/app"], buffer.getvalue())
docker(["commit", "--change", 'ENTRYPOINT ["/app/BackupNormalizer.AotTrial"]',
        "--change", 'CMD ["--run-static", "/app/ef-trial-fixtures"]', builder, image])
record["image"] = image

for label, mode in (("static", "--run-static"), ("facade", "--run")):
    container = prefix + "-" + label
    result = docker(["run", "--name", container, "--network", "none", "--read-only",
                     "--tmpfs", "/tmp:rw,size=128m", "--cap-drop", "ALL",
                     "--security-opt", "no-new-privileges", image, mode, "/app/ef-trial-fixtures"], check=False)
    stdout = result.stdout.decode("utf-8", errors="replace")
    stderr = result.stderr.decode("utf-8", errors="replace")
    inspection = json.loads(docker(["inspect", container]).stdout)[0]
    assert not inspection["Mounts"]
    case = {"mode": mode, "container": container, "exitCode": result.returncode,
            "stdout": stdout, "stderr": stderr, "oomKilled": inspection["State"]["OOMKilled"],
            "mounts": inspection["Mounts"], "passed": result.returncode == 0
            and "RESULT: 0 failed checks" in stdout and "Page size: 32768" in stdout
            and "Architecture: Arm" in stdout and "Dynamic code supported: False" in stdout}
    record["cases"].append(case)
    (output / (label + "-nas.log")).write_text(stdout + stderr, encoding="utf-8")
    print(label + ": exit " + str(result.returncode), flush=True)
    for line in stdout.splitlines():
        if line.startswith(("Runtime:", "Architecture:", "Page size:", "Dynamic code", "PASS:", "FAIL:", "RESULT:")):
            print(line[:500], flush=True)

integrity = docker(["run", "--name", prefix + "-integrity", "--network", "none", "--read-only",
                    "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
                    "--entrypoint", "/usr/bin/sha256sum", image, *["/app/" + name for name in files]])
actual = {line.split()[1].removeprefix("/app/"): line.split()[0] for line in integrity.stdout.decode().splitlines()}
record["hashesMatch"] = all(actual.get(name) == entry["sha256"] for name, entry in record["files"].items())
after = docker(["ps", "--format", "{{.Names}}"] ).stdout.decode().splitlines()
record["originalRunningServices"] = original
record["allOriginalServicesStillRunning"] = set(original).issubset(after)
(output / "nas-result.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
print("Checksums match:", record["hashesMatch"], flush=True)
print("Original services running:", record["allOriginalServicesStillRunning"], flush=True)
assert record["hashesMatch"] and record["allOriginalServicesStillRunning"]
raise SystemExit(0 if record["cases"][0]["passed"] else 1)
