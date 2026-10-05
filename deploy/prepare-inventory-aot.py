"""Copy maintained application sources into a new isolated AOT build directory."""
from pathlib import Path
import hashlib
import json
import shutil
import sys

repo = Path(__file__).resolve().parents[1]
output = Path(sys.argv[1]).resolve()
if not output.is_relative_to(repo / "publish"):
    raise ValueError("Build output must be under the repository publish directory.")
source = output / "source"
if source.exists():
    raise FileExistsError("Choose a new build directory to preserve earlier evidence.")
manifest = {}
directories = ["src/BackupNormalizer", "src/BackupNormalizer.Core", "src/BackupNormalizer.Schema",
               "src/BackupNormalizer.Migrations", "tools/BackupNormalizer.MigrationGenerator"]
for directory in directories:
    for original in (repo / directory).rglob("*"):
        if not original.is_file() or any(part in ("bin", "obj", "GeneratedModel", "GeneratedQueries")
                                         for part in original.relative_to(repo / directory).parts):
            continue
        if original.suffix not in (".cs", ".csproj", ".props"):
            continue
        relative = original.relative_to(repo)
        target = source / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(original, target)
        manifest[str(relative)] = hashlib.sha256(original.read_bytes()).hexdigest()
(source / "global.json").write_text(json.dumps({"sdk": {"version": "10.0.302", "rollForward": "disable"}}))
(output / "source-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
print(source)
