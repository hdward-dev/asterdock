"""Generate the release discovery catalog from the appbundles being published."""

import argparse
import json
from pathlib import Path
import re
import zipfile


def generate_catalog(directory: Path, release_tag: str) -> dict:
    if not release_tag.strip():
        raise ValueError("Release tag must not be empty")
    applications = []
    identifiers = set()
    for bundle in sorted(directory.glob("*.appbundle")):
        with zipfile.ZipFile(bundle) as archive:
            manifest = json.loads(archive.read("app.json"))
        identifier = manifest["id"]
        if not re.fullmatch(r"[A-Za-z0-9_.-]+", identifier):
            raise ValueError(f"Invalid application id: {identifier}")
        if identifier.lower() == "home":
            raise ValueError("The built-in home must not be published as an appbundle")
        if identifier.lower() in identifiers:
            raise ValueError(f"Duplicate application id: {identifier}")
        identifiers.add(identifier.lower())
        version = manifest["version"]
        if not re.fullmatch(r"[0-9]+(?:\.[0-9]+){1,3}", version):
            raise ValueError(f"Invalid application version: {version}")
        if not manifest["name"].strip():
            raise ValueError(f"Missing application name: {identifier}")
        applications.append({
            **{key: manifest[key] for key in ("id", "name", "description", "category", "version")},
            "releaseTag": release_tag,
            "assetName": bundle.name,
        })
    if not applications:
        raise ValueError("No appbundles found; refusing to publish an empty catalog")
    return {"schemaVersion": 1, "applications": applications}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--release-tag", required=True)
    args = parser.parse_args()
    catalog = generate_catalog(args.directory, args.release_tag)
    destination = args.directory / "app-catalog.json"
    destination.write_text(json.dumps(catalog, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Generated {destination}: {len(catalog['applications'])} applications")
