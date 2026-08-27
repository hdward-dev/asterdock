#!/usr/bin/env bash
set -euo pipefail

RID="${1:-osx-arm64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PUBLISH="$ROOT/artifacts/publish/$RID"
APP="$ROOT/artifacts/macos/$RID/星栈.app"

dotnet publish "$ROOT/src/AsterDock.Host/AsterDock.Host.csproj" \
  -c Release -r "$RID" --self-contained true -p:PublishSingleFile=true -o "$PUBLISH"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$ROOT/build/macos/Info.plist" "$APP/Contents/Info.plist"
cp "$ROOT/src/AsterDock.Host/Assets/Brand/AsterDock.icns" "$APP/Contents/Resources/AsterDock.icns"
cp -R "$PUBLISH/"* "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/AsterDock.Host"
echo "$APP"
