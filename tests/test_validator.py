import hashlib
import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from zipfile import ZipFile

from tools.validate import ValidationError, build_index


class RegistryTests(unittest.TestCase):
    def setUp(self):
        temporary = TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        (self.root / "catalog").mkdir()
        (self.root / "packages").mkdir()
        self.manifest = {
            "id": "wonder.example.hello",
            "name": "Hello",
            "description": "Greets the launcher.",
            "author": "Wonder",
            "version": "1.0.0",
            "apiVersion": 1,
            "entryAssembly": "Wonder.Example.Hello.dll",
            "entryType": "Wonder.Example.Hello.HelloPlugin",
        }
        self.package_path = "packages/wonder.example.hello-1.0.0.zip"
        self.package = self.root / self.package_path
        self.source_url = "https://github.com/TouristH/UNuse/tree/dev/examples"
        self.entry = {"package": self.package_path, "sourceUrl": self.source_url}

    def write_catalog(self, entries):
        (self.root / "catalog/plugins.json").write_text(json.dumps(entries), encoding="utf-8")

    def write_package(self, extra_files=None, include_assembly=True):
        with ZipFile(self.package, "w") as archive:
            archive.writestr("plugin.json", json.dumps(self.manifest))
            if include_assembly:
                archive.writestr(self.manifest["entryAssembly"], b"MZ fixture")
            for name, content in (extra_files or {}).items():
                archive.writestr(name, content)

    def test_index_uses_manifest_and_actual_package_bytes(self):
        self.write_catalog([self.entry])
        self.write_package({"source/HelloPlugin.cs": "// Example"})
        package = self.package.read_bytes()
        index = build_index(self.root)
        self.assertEqual(index, {
            "schemaVersion": 1,
            "product": "wonder",
            "plugins": [{
                **{key: self.manifest[key] for key in (
                    "id", "name", "description", "author", "version", "apiVersion"
                )},
                "downloadUrl": "https://raw.githubusercontent.com/TouristH/UNuse/dev/"
                + self.package_path,
                "sha256": hashlib.sha256(package).hexdigest(),
                "size": len(package),
                "sourceUrl": self.source_url,
            }],
        })

    def test_duplicate_plugin_id_is_rejected(self):
        self.write_catalog([self.entry, self.entry])
        self.write_package()
        with self.assertRaisesRegex(ValidationError, "duplicate plugin ID"):
            build_index(self.root)

    def test_unsafe_archive_path_is_rejected(self):
        self.write_catalog([self.entry])
        self.write_package({"../outside.txt": "content"})
        with self.assertRaisesRegex(ValidationError, "unsafe ZIP path"):
            build_index(self.root)

    def test_missing_entry_assembly_is_rejected(self):
        self.write_catalog([self.entry])
        self.write_package(include_assembly=False)
        with self.assertRaisesRegex(ValidationError, "entryAssembly"):
            build_index(self.root)

    def test_catalog_package_path_cannot_escape_root(self):
        self.write_catalog([{**self.entry, "package": "../outside.zip"}])
        with self.assertRaisesRegex(ValidationError, "package path"):
            build_index(self.root)

    def test_api_version_must_be_an_integer(self):
        self.write_catalog([self.entry])
        self.manifest["apiVersion"] = True
        self.write_package()
        with self.assertRaisesRegex(ValidationError, "apiVersion"):
            build_index(self.root)


if __name__ == "__main__":
    unittest.main()
