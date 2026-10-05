"""Execute PC-prepared plans through the generated managed query interceptors."""
import importlib.util
from pathlib import Path
import subprocess
import sys

output = Path(sys.argv[1]).resolve()
directory = Path(__file__).resolve().parent
cli = output / "source/src/BackupNormalizer/bin/Release/net10.0/BackupNormalizer"
attempt = sys.argv[2] if len(sys.argv) > 2 else "managed-execution"
if Path(attempt).name != attempt:
    raise ValueError("Pass a fresh directory name inside the build output.")
work = output / attempt
result = subprocess.run(["sh", str(directory / "execution-aot-acceptance.sh"), str(cli),
                         str(output / "execution-fixture"), str(work)], capture_output=True, text=True)
stdout = result.stdout
if "EXECUTION_BASE64_BEGIN\n" in stdout:
    encoded = stdout.split("EXECUTION_BASE64_BEGIN\n", 1)[1].split("EXECUTION_BASE64_END", 1)[0]
    stdout = stdout.replace(encoded, "[portable execution database retained locally]\n")
log = "precompiled-execution.log" if attempt == "managed-execution" else "precompiled-execution-" + attempt + ".log"
(output / log).write_text(stdout + result.stderr)
if result.returncode:
    raise RuntimeError(stdout + result.stderr)
spec = importlib.util.spec_from_file_location("execution_database", directory / "verify-execution-database.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
print(module.verify_database(work / "state/portable.db", str(work / "target"), str(work / "source")))
print("Precompiled execution: PC plans, confirmation, replay, conflicts, recovery and journals passed")
