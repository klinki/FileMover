"""Prepare an isolated copy of the application for the EF AOT feasibility trial."""
import hashlib
import json
from pathlib import Path
import shutil
import sys

REPO = Path(__file__).resolve().parents[2]
output = Path(sys.argv[1]).resolve()
if not output.is_relative_to(REPO / "publish"):
    raise ValueError("The trial output must be under the repository's publish directory.")
source = output / "source"
if source.exists():
    raise FileExistsError("Use a new output directory; previous trial evidence is preserved.")
manifest = {}
for directory in ("src/BackupNormalizer.Core", "tools/BackupNormalizer.AotTrial"):
    for original in (REPO / directory).rglob("*"):
        if not original.is_file() or any(part in ("bin", "obj", "__pycache__") for part in original.relative_to(REPO / directory).parts):
            continue
        if original.suffix not in (".cs", ".csproj", ".props"):
            continue
        relative = original.relative_to(REPO)
        target = source / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(original, target)
        manifest[str(relative)] = hashlib.sha256(original.read_bytes()).hexdigest()
output.mkdir(parents=True, exist_ok=True)
(source / "global.json").write_text(json.dumps({"sdk": {"version": "10.0.302", "rollForward": "disable"}}), encoding="utf-8")
(source / "NuGet.Config").write_text('''<configuration><packageSources><clear/><add key="cache" value="E:/packages/nuget"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources><config><add key="globalPackagesFolder" value="../windows-packages"/></config></configuration>''', encoding="utf-8")
(output / "source-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
print(f"Snapshot: {source}; {len(manifest)} source files")
