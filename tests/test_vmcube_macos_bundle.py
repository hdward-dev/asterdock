"""Exercise relocation of signed Mach-O files without requiring SPICE or QEMU."""
import pathlib
import platform
import subprocess
import sys
import tempfile
import unittest


@unittest.skipUnless(sys.platform == 'darwin', 'requires macOS signing tools')
class MacBundleTests(unittest.TestCase):
    def test_signed_executable_and_library(self):
        script = pathlib.Path(__file__).resolve().parents[1] / 'scripts/bundle-vmcube-native.py'
        with tempfile.TemporaryDirectory(prefix='asterdock-macho-') as directory:
            root = pathlib.Path(directory)
            (root / 'library.c').write_text('int value(void) { return 42; }\n')
            (root / 'main.c').write_text('extern int value(void); int main(void) { return value() == 42 ? 0 : 1; }\n')
            for arch in ('arm64', 'x86_64'):
                with self.subTest(arch=arch):
                    source = root / arch
                    source.mkdir()
                    library, executable = source / 'libfixture.dylib', source / 'fixture'
                    subprocess.run(['clang', '-arch', arch, '-dynamiclib', str(root / 'library.c'),
                                    '-install_name', str(library), '-o', str(library)], check=True)
                    subprocess.run(['clang', '-arch', arch, str(root / 'main.c'), str(library), '-o', str(executable)], check=True)
                    for file in (library, executable):
                        subprocess.run(['codesign', '--force', '--sign', '-', str(file)], check=True)
                    output = source / 'bundled'
                    subprocess.run([sys.executable, str(script), str(executable), str(output)], check=True)
                    # Original dylib must not be available to hide a relocation failure.
                    library.unlink()
                    for name in ('fixture', 'libfixture.dylib'):
                        subprocess.run(['codesign', '--verify', '--strict', str(output / name)], check=True)
                    linked = subprocess.check_output(['otool', '-L', str(output / 'fixture')], text=True)
                    self.assertIn('@loader_path/libfixture.dylib', linked)
                    if arch == platform.machine():
                        subprocess.run([str(output / 'fixture')], check=True)
