"""Build the maintained runtime Dockerfile through the authorized NAS Docker engine."""
import argparse
from datetime import datetime, timezone
import io
import json
from pathlib import Path
import shlex
import subprocess
import tarfile
from urllib.parse import urlencode

parser = argparse.ArgumentParser()
parser.add_argument("output", type=Path)
parser.add_argument("--host", required=True)
args = parser.parse_args()
repo = Path(__file__).resolve().parents[1]
output = args.output.resolve()
if not output.is_relative_to(repo / "publish"):
    raise ValueError("Output must be under the repository publish directory.")
result_path = output / "runtime-image-result.json"
if result_path.exists():
    raise FileExistsError("Preserve earlier results; use a new output directory.")
image = "backup-normalizer:inventory-aot-runtime-" + datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
files = {name: output / "app" / name for name in ("BackupNormalizer", "BackupNormalizer.Migrations", "libe_sqlite3.so")}
files.update({name: repo / name for name in ("Dockerfile.qnap", "qnap-check.sh", "qnap-inventory.sh")})
buffer = io.BytesIO()
with tarfile.open(fileobj=buffer, mode="w") as archive:
    for name, path in files.items():
        info = archive.gettarinfo(str(path), arcname="Dockerfile" if name == "Dockerfile.qnap" else name)
        info.mode = 0o755 if name.startswith("BackupNormalizer") or name.endswith(".sh") else 0o644
        with path.open("rb") as source:
            archive.addfile(info, source)
query = urlencode({"t": image, "dockerfile": "Dockerfile", "platform": "linux/arm/v7"})
# Stream to the daemon: the Docker CLI otherwise stages stdin under the NAS's small /tmp.
remote = shlex.join(["/sbin/curl", "--unix-socket", "/var/run/docker.sock", "--fail-with-body",
                     "--silent", "--show-error", "--header", "Content-Type: application/x-tar",
                     "--data-binary", "@-", "http://localhost/build?" + query])
result = subprocess.run(["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "admin@" + args.host, remote],
                        input=buffer.getvalue(), capture_output=True)
(output / "runtime-image-build.log").write_text((result.stdout + result.stderr).decode("utf-8", errors="replace"))
messages = [json.loads(line) for line in result.stdout.decode("utf-8", errors="replace").splitlines() if line.strip()]
errors = [message["error"] for message in messages if "error" in message]
exit_code = result.returncode or (1 if errors else 0)
result_path.write_text(json.dumps({"image": image, "exitCode": exit_code, "errors": errors}, indent=2) + "\n")
print("Image:", image)
print("Build exit:", exit_code)
for error in errors:
    print(error)
raise SystemExit(exit_code)
