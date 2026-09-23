import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile


ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("catalog", ROOT / "scripts/generate-app-catalog.py")
catalog = importlib.util.module_from_spec(spec)
spec.loader.exec_module(catalog)


class CatalogTests(unittest.TestCase):
    def write_bundle(self, directory, filename, manifest):
        with zipfile.ZipFile(directory / filename, "w") as archive:
            archive.writestr("app.json", json.dumps(manifest))

    def test_all_installable_manifests_match_release_assets(self):
        manifests = [json.loads(path.read_text(encoding="utf-8")) for path in ROOT.glob("src/*.Module/app.json")]
        expected = {m["id"]: m for m in manifests if m["id"] != "home"}
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            for identifier, manifest in expected.items():
                self.write_bundle(directory, f"AsterDock-App-{identifier}.appbundle", manifest)
            result = catalog.generate_catalog(directory, "nightly-test")
            self.assertEqual(result["schemaVersion"], 1)
            self.assertEqual({app["id"] for app in result["applications"]}, set(expected))
            for app in result["applications"]:
                self.assertEqual(app["releaseTag"], "nightly-test")
                self.assertTrue((directory / app["assetName"]).exists())
                for key, value in expected[app["id"]].items():
                    if key in app:
                        self.assertEqual(app[key], value)

    def test_empty_directory_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaisesRegex(ValueError, "No appbundles"):
                catalog.generate_catalog(Path(temporary), "nightly-test")

    def test_duplicate_and_home_are_rejected(self):
        manifest = json.loads((ROOT / "src/InvoicePrinter.Module/app.json").read_text(encoding="utf-8"))
        for identifier in (manifest["id"].upper(), "home"):
            with self.subTest(identifier=identifier), tempfile.TemporaryDirectory() as temporary:
                directory = Path(temporary)
                self.write_bundle(directory, "first.appbundle", manifest)
                self.write_bundle(directory, "second.appbundle", {**manifest, "id": identifier})
                with self.assertRaises(ValueError):
                    catalog.generate_catalog(directory, "nightly-test")


if __name__ == "__main__":
    unittest.main()
