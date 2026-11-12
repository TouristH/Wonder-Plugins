"""Build and package the Wonder Hello example."""

import argparse
import json
from pathlib import Path
import subprocess
from zipfile import ZIP_DEFLATED, ZipFile, ZipInfo


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--contracts", type=Path, required=True)
    options = parser.parse_args()
    contracts = options.contracts.resolve()
    if not contracts.is_file():
        parser.error(f"contract assembly not found: {contracts}")

    root = Path(__file__).resolve().parent.parent
    sample = root / "examples/Wonder.Example.Hello"
    project = sample / "Wonder.Example.Hello.csproj"
    manifest = sample / "plugin.json"
    metadata = json.loads(manifest.read_text(encoding="utf-8"))
    subprocess.run(
        ["dotnet", "build", str(project), "-c", "Release", "--nologo",
         f"-p:WonderContractsPath={contracts}", "-p:DebugType=None", "-p:DebugSymbols=false"],
        check=True,
    )
    assembly = metadata["entryAssembly"]
    output = root / "packages" / f"{metadata['id']}-{metadata['version']}.zip"
    output.parent.mkdir(exist_ok=True)
    sources = (project.name, "HelloPlugin.cs", manifest.name)
    licenses = ("LICENSE", "ADDITIONAL_TERMS.md", "BRANDING.md", "NOTICE")
    entries = {
        manifest.name: manifest,
        assembly: sample / "bin/Release/net10.0" / assembly,
        **{f"source/{name}": sample / name for name in sources},
        **{name: sample / name for name in licenses},
    }
    with ZipFile(output, "w") as archive:
        for name, path in sorted(entries.items()):
            archive.writestr(ZipInfo(name), path.read_bytes(), compress_type=ZIP_DEFLATED)
    print(output)


if __name__ == "__main__":
    main()
