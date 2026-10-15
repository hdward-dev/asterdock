#!/usr/bin/env python3
"""Stage a SPICE worker and optional QEMU runtime with relocatable shared libraries.
Linux/macOS: python bundle-vmcube-native.py WORKER OUTPUT [--qemu /path/qemu-system-x86_64]
Windows (MSYS2): dependencies are resolved through objdump and PATH.
"""
import argparse, collections, json, os, pathlib, platform, re, shutil, subprocess
p = argparse.ArgumentParser(); p.add_argument('worker'); p.add_argument('output'); p.add_argument('--qemu'); args = p.parse_args()
out = pathlib.Path(args.output).resolve(); out.mkdir(parents=True, exist_ok=True)
system = platform.system(); windows = system == 'Windows' or system.startswith(('MINGW', 'MSYS'))
manifest = []

def copy_file(source, destination):
    destination = pathlib.Path(destination)
    if destination.is_file(): destination.chmod(destination.stat().st_mode | 0o200)
    shutil.copy2(source, destination)
    destination.chmod(destination.stat().st_mode | 0o200)
    return str(destination)

def run(*command): return subprocess.check_output(command, text=True, stderr=subprocess.STDOUT)

def stage(source, target):
    original_name = pathlib.Path(source).name
    source = pathlib.Path(source).resolve()
    # Nix wraps QEMU for its own store layout; package the actual ELF, not that wrapper.
    unwrapped = source.with_name('.' + source.name + '-wrapped')
    if unwrapped.is_file(): source = unwrapped
    target.mkdir(parents=True, exist_ok=True)
    queue = collections.deque([(source, source.name.lstrip('.').removesuffix('-wrapped') if unwrapped == source else original_name)])
    copied, links = {}, {}
    while queue:
        src, name = queue.popleft()
        if name in copied: continue
        dst = target / name; copy_file(src, dst); dst.chmod(0o755); copied[name] = dst
        manifest.append({'file': str(dst.relative_to(out)), 'source': str(src)})
        if windows:
            names = re.findall(r'DLL Name:\s*(\S+)', run('objdump', '-p', str(src)))
            dependencies = []
            for name in names:
                found = next((pathlib.Path(d) / name for d in os.environ['PATH'].split(os.pathsep) if (pathlib.Path(d) / name).is_file()), None)
                if found: dependencies.append((name, str(found)))
        elif system == 'Darwin':
            dependencies = []
            for line in run('otool', '-L', str(src)).splitlines()[1:]:
                name = line.strip().split(' (', 1)[0]
                if name.startswith(('/System/', '/usr/lib/')): continue
                path = name.replace('@loader_path', str(src.parent))
                if path.startswith('@rpath/'):
                    candidates = [src.parent / pathlib.Path(path).name, pathlib.Path('/opt/homebrew/lib') / pathlib.Path(path).name, pathlib.Path('/usr/local/lib') / pathlib.Path(path).name]
                    path = str(next((x for x in candidates if x.exists()), pathlib.Path(path)))
                if not pathlib.Path(path).exists(): raise RuntimeError('Unresolved dylib: ' + name)
                dependencies.append((name, path))
        else:
            text = run('ldd', str(src))
            if 'not found' in text: raise RuntimeError(text)
            dependencies = re.findall(r'^\s*(\S+)\s+=>\s+(/\S+)', text, re.M)
            dependencies = [(name, path) for name, path in dependencies if not re.match(r'^(libc\.|libm\.|libpthread\.|libdl\.|librt\.|libresolv\.|libutil\.|ld-linux)', name)]
        links[dst] = dependencies
        for original, path in dependencies: queue.append((pathlib.Path(path).resolve(), pathlib.Path(original).name))
    for dst, dependencies in links.items():
        if windows: continue
        if system == 'Darwin':
            subprocess.run(['codesign', '--remove-signature', str(dst)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            if dst.suffix == '.dylib': subprocess.check_call(['install_name_tool', '-id', '@rpath/' + dst.name, str(dst)])
            for original, path in dependencies: subprocess.check_call(['install_name_tool', '-change', original, '@loader_path/' + pathlib.Path(original).name, str(dst)])
            subprocess.check_call(['codesign', '--force', '--sign', '-', str(dst)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        else: subprocess.check_call(['patchelf', '--set-rpath', '$ORIGIN', str(dst)])

stage(args.worker, out)
if args.qemu:
    qemu = pathlib.Path(args.qemu).resolve(); target = out / 'qemu'
    stage(qemu, target); stage(qemu.with_name('qemu-img' + qemu.suffix), target)
    # Nix may provide SDL2 via sdl2-compat, which dlopens SDL3 rather than linking it.
    if system == 'Linux' and any('sdl2-compat' in x['source'] for x in manifest):
        sdl3 = next(iter(pathlib.Path('/nix/store').glob('*sdl3*-lib/lib/libSDL3.so.0')), None)
        if sdl3 is None: raise RuntimeError('sdl2-compat needs an SDL3 runtime')
        stage(sdl3, target)
    firmware = qemu.parent.parent / 'share' / 'qemu'; dest = target / 'share' / 'qemu'; dest.mkdir(parents=True, exist_ok=True)
    firmware_roots = [firmware, pathlib.Path('/usr/share/seabios'), pathlib.Path('/usr/share/vgabios')]
    for pattern in ['bios*.bin', 'vgabios*.bin', '*e1000*.rom', '*virtio*.rom', 'kvmvapic.bin', 'linuxboot*.bin', 'multiboot*.bin', '*license*', '*LICENSE*']:
        for firmware_root in firmware_roots:
            for source in firmware_root.glob(pattern):
                if source.is_file():
                    destination = dest / source.name
                    if destination.exists(): destination.unlink()
                    copy_file(source, destination); destination.chmod(0o644)
    if (firmware / 'keymaps').is_dir(): shutil.copytree(firmware / 'keymaps', dest / 'keymaps', dirs_exist_ok=True, copy_function=copy_file)
    module_roots = [qemu.parent.parent / 'lib' / 'qemu', *pathlib.Path('/usr/lib').glob('*/qemu')]
    for module_root in module_roots:
        for pattern in ['accel-tcg-*', 'hw-display-qxl.*', 'ui-spice-core.*', 'net-slirp.*']:
            for module in module_root.glob(pattern):
                if module.is_file(): stage(module, target / 'modules')
    if not (dest / 'bios-256k.bin').exists() and not (dest / 'bios.bin').exists(): raise RuntimeError('Missing x86 BIOS firmware: ' + str(firmware))
if args.qemu: (out / 'qemu' / 'native-dependencies.json').write_text(json.dumps([x for x in manifest if x['file'].startswith('qemu/')], indent=2) + '\n')
license_root = pathlib.Path(__file__).resolve().parent.parent / 'src' / 'VMCube.Module'
for destination in [out, *([out / 'qemu'] if args.qemu else [])]:
    if (license_root / 'THIRD-PARTY-NOTICES.md').exists(): shutil.copy2(license_root / 'THIRD-PARTY-NOTICES.md', destination / 'THIRD-PARTY-NOTICES.md')
    if (license_root / 'licenses').is_dir(): shutil.copytree(license_root / 'licenses', destination / 'licenses', dirs_exist_ok=True)
# Debian/Ubuntu provides package-specific copyright and license notices.
if system == 'Linux' and shutil.which('dpkg-query'):
    notices = set()
    for item in manifest:
        found = subprocess.run(['dpkg-query', '-S', item['source']], capture_output=True, text=True)
        for line in found.stdout.splitlines():
            package = line.split(': ', 1)[0].split(':', 1)[0]
            notice = pathlib.Path('/usr/share/doc') / package / 'copyright'
            if notice.is_file(): notices.add((package, notice))
    for package, notice in notices:
        for destination in [out, *([out / 'qemu'] if args.qemu else [])]:
            (destination / 'licenses').mkdir(exist_ok=True)
            shutil.copy2(notice, destination / 'licenses' / (package + '.copyright'))
(out / 'native-dependencies.json').write_text(json.dumps(manifest, indent=2) + '\n')
print('Staged native files:', len(manifest), 'at', out)
