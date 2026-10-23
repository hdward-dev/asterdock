"""Check executable resolution independently of host OS and native dependencies."""
import ast
import pathlib
import tempfile
import unittest


class ExecutablePathTests(unittest.TestCase):
    def resolve(self, value, windows):
        script = pathlib.Path(__file__).resolve().parents[1] / 'scripts/bundle-vmcube-native.py'
        tree = ast.parse(script.read_text())
        function = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == 'executable_path')
        scope = {'pathlib': pathlib, 'windows': windows}
        exec(compile(ast.Module(body=[function], type_ignores=[]), str(script), 'exec'), scope)
        return scope['executable_path'](value)

    def test_msys_executable_without_extension(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / 'qemu-system-x86_64.exe'
            path.touch()
            self.assertEqual(self.resolve(path.with_suffix(''), True), path.resolve())
            self.assertEqual(self.resolve(path, True), path.resolve())
            with self.assertRaises(FileNotFoundError): self.resolve(path.with_suffix(''), False)

    def test_missing_executable_reports_path(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(FileNotFoundError, 'Missing native executable:'):
                self.resolve(pathlib.Path(directory) / 'missing', True)


class SpicePackageAssetTests(unittest.TestCase):
    def test_spice_zip_follows_qemu_removal_and_keeps_the_helper(self):
        script = (pathlib.Path(__file__).resolve().parents[1] / 'scripts/package-apps.ps1').read_text()
        qemu = script.index('VMCUBE-QEMU-')
        remove_qemu = script.index('Remove-Item -LiteralPath $engine')
        spice = script.index('VMCUBE-SPICE-$')
        self.assertLess(qemu, remove_qemu)
        self.assertLess(remove_qemu, spice)
        self.assertIn('vmcube-spice', script)
        self.assertNotIn('Remove-Item -LiteralPath $helper', script)
