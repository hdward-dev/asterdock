#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
case "$(uname -s):$(uname -m)" in
  Linux:x86_64) RID=linux-x64 ;; Linux:aarch64|Linux:arm64) RID=linux-arm64 ;;
  Darwin:x86_64) RID=osx-x64 ;; Darwin:arm64) RID=osx-arm64 ;;
  MINGW*:x86_64|MSYS*:x86_64) RID=win-x64 ;; *) echo 'Unsupported native platform' >&2; exit 2 ;;
esac
OUTPUT="${1:-$ROOT/src/VMCube.Module/Native/bin/$RID}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
GENERATOR=(); if command -v ninja >/dev/null 2>&1; then GENERATOR=(-G Ninja); fi
cmake "${GENERATOR[@]}" -S "$ROOT/src/VMCube.Module/Native" -B "$WORK" -DCMAKE_BUILD_TYPE=Release
cmake --build "$WORK" --parallel 2
EXE=vmcube-spice; if [[ "$RID" == win-* ]]; then EXE=vmcube-spice.exe; fi
QEMU="$(command -v qemu-system-x86_64 || true)"
if [[ -n "$QEMU" ]]; then
  python3 "$ROOT/scripts/bundle-vmcube-native.py" "$WORK/$EXE" "$OUTPUT" --qemu "$QEMU"
else
  python3 "$ROOT/scripts/bundle-vmcube-native.py" "$WORK/$EXE" "$OUTPUT"
fi
