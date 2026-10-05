"""Enable EF AOT generation only in the previously prepared experiment snapshot."""
from pathlib import Path
import sys

REPO = Path(__file__).resolve().parents[2]
output = Path(sys.argv[1]).resolve()
if not output.is_relative_to(REPO / "publish"):
    raise ValueError("The trial output must be under the repository's publish directory.")
core = output / "source/src/BackupNormalizer.Core"
database = core / "Database.cs"
text = database.read_text(encoding="utf-8")
anchor = "                Context.Database.Migrate();"
if text.count(anchor) != 1:
    raise ValueError("Expected exactly one migration call in the snapshot.")
database.write_text(text.replace(anchor, "                // AOT trial uses a pre-migrated fixture; production migration startup is tested separately."), encoding="utf-8")
for project in (core / "BackupNormalizer.Core.csproj", output / "source/tools/BackupNormalizer.AotTrial/BackupNormalizer.AotTrial.csproj"):
    text = project.read_text(encoding="utf-8")
    addition = '''
  <PropertyGroup>
    <InterceptorsNamespaces>$(InterceptorsNamespaces);Microsoft.EntityFrameworkCore.GeneratedInterceptors</InterceptorsNamespaces>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Tasks" Version="10.0.12" PrivateAssets="all" />
  </ItemGroup>
'''
    if "--manual" in sys.argv:
        addition = '''
  <PropertyGroup>
    <InterceptorsNamespaces>$(InterceptorsNamespaces);Microsoft.EntityFrameworkCore.GeneratedInterceptors</InterceptorsNamespaces>
  </PropertyGroup>
'''
        if project.parent != core:
            addition += '''
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.12" PrivateAssets="all" />
  </ItemGroup>
'''
    project.write_text(text.replace("</Project>", addition + "</Project>"), encoding="utf-8")
    if "--static" in sys.argv:
        text = project.read_text(encoding="utf-8")
        stage = "EFPrecompileQueriesStage" if project.parent == core else "EFScaffoldModelStage"
        project.write_text(text.replace("</Project>", f"<PropertyGroup><{stage}>none</{stage}></PropertyGroup></Project>"), encoding="utf-8")
    if project.parent != core:
        text = project.read_text(encoding="utf-8")
        project.write_text(text.replace("</Project>", '''
  <PropertyGroup Condition="'$(PublishAot)' != 'true'">
    <UseAppHost>false</UseAppHost>
    <SelfContained>false</SelfContained>
  </PropertyGroup>
</Project>'''), encoding="utf-8")
print("Enabled EF model/query generation and bypassed migrations only in the experiment snapshot.")
