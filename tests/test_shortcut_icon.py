import importlib.util
from pathlib import Path
import struct
import unittest


ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("shortcut_icon", ROOT / "scripts/build-shortcut-icon.py")
shortcut_icon = importlib.util.module_from_spec(spec)
spec.loader.exec_module(shortcut_icon)


def ico_images(data: bytes) -> list[bytes]:
    _, _, count = struct.unpack_from("<HHH", data, 0)
    images = []
    for index in range(count):
        _, _, _, _, _, _, size, offset = struct.unpack_from("<BBBBHHII", data, 6 + 16 * index)
        images.append(data[offset : offset + size])
    return images


class ShortcutIconTests(unittest.TestCase):
    def setUp(self):
        self.ico_path = ROOT / "src/AsterDock.Host/Assets/Brand/AsterDock.ico"
        self.ico = self.ico_path.read_bytes()

    def test_pe_contains_every_brand_icon_image(self):
        executable = shortcut_icon.build_icon_executable(self.ico)
        self.assertTrue(executable.startswith(b"MZ"))
        self.assertEqual(shortcut_icon.icon_images(executable), ico_images(self.ico))
        group = shortcut_icon.group_icon_directory(executable)
        self.assertEqual(len(group), 7)
        self.assertTrue(all(entry["planes"] == 1 and entry["bit_count"] == 32 for entry in group))
        self.assertEqual([entry["id"] for entry in group], list(range(1, 8)))
        self.assertEqual(shortcut_icon.build_icon_executable(self.ico), executable)

    def test_installer_points_both_shortcuts_at_the_brand_icon(self):
        source = (ROOT / "build/windows/AsterDock.wxs").read_text(encoding="utf-8")
        package = (ROOT / "scripts/package-windows.ps1").read_text(encoding="utf-8")
        self.assertIn('SourceFile="$(var.BrandIcon)"', source)
        self.assertIn('Id="ARPPRODUCTICON" Value="AsterDock.exe"', source)
        self.assertIn('<StandardDirectory Id="DesktopFolder" />', source)
        for shortcut in ("StartMenuShortcut", "DesktopShortcut"):
            element = source.split(f'Id="{shortcut}"', 1)[1].split("/>", 1)[0]
            self.assertIn('Name="AsterDock"', element)
            self.assertIn('Icon="AsterDock.exe"', element)
            self.assertIn('IconIndex="0"', element)
            self.assertIn('Advertise="yes"', element)
        self.assertIn('Directory="ProgramMenuFolder"', source)
        self.assertIn('Directory="DesktopFolder"', source)
        self.assertIn("src/AsterDock.Host/Assets/Brand/AsterDock.ico", package)
        self.assertIn("scripts/build-shortcut-icon.py", package)
        self.assertIn('-d "BrandIcon=$brandIcon"', package)

    def test_truncated_icon_is_rejected(self):
        with self.assertRaises(ValueError):
            shortcut_icon.build_icon_executable(b"not an icon")
