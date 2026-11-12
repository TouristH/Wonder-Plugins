"""Build the Wonder plugin catalog from checked-in release ZIPs."""

import argparse
import hashlib
import io
import json
from pathlib import Path
import re
import stat
import sys
from urllib.parse import urlsplit
from zipfile import BadZipFile, ZipFile


RAW_BASE_URL = "https://raw.githubusercontent.com/TouristH/UNuse/dev/"
INDEX_PATH = Path("public/wonder/v1/index.json")
MAX_JSON_BYTES = 1024 * 1024
MAX_PACKAGE_BYTES = 64 * 1024 * 1024
MAX_EXPANDED_BYTES = 128 * 1024 * 1024
MAX_ENTRIES = 2048
PLUGIN_ID = re.compile(r"[a-z0-9][a-z0-9._-]{0,79}")
VERSION = re.compile(r"(?:0|[1-9][0-9]{0,9})(?:\.(?:0|[1-9][0-9]{0,9})){2}")
PACKAGE_PATH = re.compile(r"packages/[a-z0-9][a-z0-9._-]{0,111}\.zip")
DEVICE_NAMES = {"con", "prn", "aux", "nul", *(f"com{i}" for i in range(1, 10)),
                *(f"lpt{i}" for i in range(1, 10))}
INDEX_FIELDS = ("id", "name", "description", "author", "version", "apiVersion")


class ValidationError(ValueError):
    pass


def unique_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValidationError(f"duplicate JSON property {key!r}")
        value[key] = item
    return value


def parse_json(payload, label):
    if len(payload) > MAX_JSON_BYTES:
        raise ValidationError(f"{label} exceeds 1 MiB")
    try:
        return json.loads(payload.decode("utf-8"), object_pairs_hook=unique_object)
    except (ValueError, RecursionError) as error:
        raise ValidationError(f"{label}: {error}") from error


def valid_path_segment(segment):
    return (segment and not segment.endswith((".", " "))
            and segment.split(".", 1)[0].casefold() not in DEVICE_NAMES
            and not any(character in '<>:"\\|?*' or ord(character) < 32
                        for character in segment))


def zip_path(info):
    name = info.orig_filename.removesuffix("/") if info.is_dir() else info.orig_filename
    if not all(valid_path_segment(segment) for segment in name.split("/")):
        raise ValidationError(f"unsafe ZIP path {info.orig_filename!r}")
    return name


def require_text(fields, key, limit):
    value = fields[key]
    if not isinstance(value, str) or not value.strip():
        raise ValidationError(f"invalid {key}")
    try:
        if len(value.encode("utf-16-le")) // 2 <= limit:
            return value
    except UnicodeError:
        pass
    raise ValidationError(f"invalid {key}")


def read_manifest(archive, files):
    manifest_info = files.get("plugin.json")
    if manifest_info is None or manifest_info.file_size > MAX_JSON_BYTES:
        raise ValidationError("ZIP must contain a root plugin.json of at most 1 MiB")
    manifest = parse_json(archive.read(manifest_info), "plugin.json")
    if not isinstance(manifest, dict) or set(manifest) != {*INDEX_FIELDS, "entryAssembly", "entryType"}:
        raise ValidationError("plugin.json must have the eight Wonder v1 manifest fields")

    plugin_id = require_text(manifest, "id", 80)
    if not PLUGIN_ID.fullmatch(plugin_id) or not valid_path_segment(plugin_id):
        raise ValidationError(f"invalid plugin id {plugin_id!r}")
    version = require_text(manifest, "version", 32)
    if not VERSION.fullmatch(version) or any(int(part) > 2147483647 for part in version.split(".")):
        raise ValidationError(f"invalid version {version!r}")
    if type(manifest["apiVersion"]) is not int or manifest["apiVersion"] != 1:
        raise ValidationError("apiVersion must be 1")
    for key, limit in (("name", 256), ("description", 4096), ("author", 256), ("entryType", 512)):
        require_text(manifest, key, limit)
    assembly = require_text(manifest, "entryAssembly", 512)
    if assembly not in files or not assembly.lower().endswith(".dll"):
        raise ValidationError("entryAssembly must name a DLL inside the ZIP")
    with archive.open(files[assembly]) as stream:
        if stream.read(2) != b"MZ":
            raise ValidationError("entryAssembly must be a PE assembly")
    if any(name.rsplit("/", 1)[-1].casefold() == "wonder.extensibility.contracts.dll"
           for name in files):
        raise ValidationError("ZIP must use the host's Wonder.Extensibility.Contracts assembly")
    return manifest


def read_package(payload):
    try:
        with ZipFile(io.BytesIO(payload)) as archive:
            entries = archive.infolist()
            if not 1 <= len(entries) <= MAX_ENTRIES:
                raise ValidationError(f"ZIP must contain 1 to {MAX_ENTRIES} entries")
            if sum(entry.file_size for entry in entries) > MAX_EXPANDED_BYTES:
                raise ValidationError("ZIP exceeds 128 MiB expanded")
            paths = {}
            files = {}
            for entry in entries:
                name = zip_path(entry)
                folded = name.casefold()
                if folded in paths:
                    raise ValidationError(f"duplicate ZIP path {name!r}")
                paths[folded] = entry
                kind = stat.S_IFMT(entry.external_attr >> 16)
                if (entry.flag_bits & 1 or entry.external_attr & 0x400
                        or kind not in (0, stat.S_IFDIR if entry.is_dir() else stat.S_IFREG)):
                    raise ValidationError(f"encrypted or special ZIP entry {name!r}")
                if not entry.is_dir():
                    files[name] = entry
            for name in paths:
                parent = name.rpartition("/")[0]
                while parent:
                    if parent in paths and not paths[parent].is_dir():
                        raise ValidationError(f"ZIP file is also used as a directory: {parent!r}")
                    parent = parent.rpartition("/")[0]
            manifest = read_manifest(archive, files)
            damaged = archive.testzip()
            if damaged is not None:
                raise ValidationError(f"corrupt ZIP entry {damaged!r}")
            return manifest
    except (BadZipFile, EOFError, NotImplementedError, OSError, RuntimeError) as error:
        raise ValidationError(f"invalid ZIP: {error}") from error


def read_catalog_entry(root, entry):
    if not isinstance(entry, dict) or set(entry) != {"package", "sourceUrl"}:
        raise ValidationError("catalog entry must contain package and sourceUrl")
    package_path = entry["package"]
    if not isinstance(package_path, str) or not PACKAGE_PATH.fullmatch(package_path):
        raise ValidationError(f"invalid package path {package_path!r}")
    source = require_text(entry, "sourceUrl", MAX_JSON_BYTES)
    try:
        parsed = urlsplit(source)
        if (parsed.scheme != "https" or not parsed.hostname or parsed.username is not None
                or any(character.isspace() or ord(character) < 32 for character in source)):
            raise ValueError()
    except ValueError as error:
        raise ValidationError("sourceUrl must be an absolute HTTPS URL without credentials") from error

    path = root / package_path
    if path.is_symlink() or not path.resolve().is_relative_to(root.resolve()):
        raise ValidationError(f"package path is outside the catalog: {package_path}")
    if not 0 < path.stat().st_size <= MAX_PACKAGE_BYTES:
        raise ValidationError(f"{package_path} must contain at most 64 MiB")
    payload = path.read_bytes()
    manifest = read_package(payload)
    expected_path = f"packages/{manifest['id']}-{manifest['version']}.zip"
    if package_path != expected_path:
        raise ValidationError(f"package path must be {expected_path}")
    return {
        **{key: manifest[key] for key in INDEX_FIELDS},
        "downloadUrl": RAW_BASE_URL + package_path,
        "sha256": hashlib.sha256(payload).hexdigest(),
        "size": len(payload),
        "sourceUrl": source,
    }


def build_index(root):
    catalog_path = root / "catalog/plugins.json"
    if catalog_path.stat().st_size > MAX_JSON_BYTES:
        raise ValidationError("catalog/plugins.json exceeds 1 MiB")
    entries = parse_json(catalog_path.read_bytes(), "catalog/plugins.json")
    if not isinstance(entries, list):
        raise ValidationError("catalog/plugins.json must be an array")
    plugins = {}
    for entry in entries:
        plugin = read_catalog_entry(root, entry)
        plugin_id = plugin["id"]
        if plugin_id in plugins:
            raise ValidationError(f"duplicate plugin ID {plugin_id!r}")
        plugins[plugin_id] = plugin
    return {"schemaVersion": 1, "product": "wonder", "plugins": [plugins[key] for key in sorted(plugins)]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    operation = parser.add_mutually_exclusive_group(required=True)
    operation.add_argument("--write", action="store_true", help="generate the public index")
    operation.add_argument("--check", action="store_true", help="verify the committed public index")
    options = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    index_path = root / INDEX_PATH
    try:
        index = build_index(root)
        rendered = (json.dumps(index, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
        if len(rendered) > MAX_JSON_BYTES:
            raise ValidationError("generated index exceeds 1 MiB")
        if options.write:
            index_path.parent.mkdir(parents=True, exist_ok=True)
            index_path.write_bytes(rendered)
        elif index_path.read_bytes() != rendered:
            raise ValidationError(f"stale index, run {parser.prog} --write")
        print(f"Validated {len(index['plugins'])} Wonder plugin(s).")
        return 0
    except (ValidationError, OSError) as error:
        print(error, file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
