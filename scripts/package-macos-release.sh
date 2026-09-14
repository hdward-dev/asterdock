#!/usr/bin/env bash
set -euo pipefail

RID="${1:-osx-arm64}"
VERSION="${2:-1.0.0}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUTPUT_DIRECTORY="${3:-$ROOT/artifacts/release/$RID}"
WORK_DIRECTORY="$(mktemp -d)"
PUBLISH_DIRECTORY="$WORK_DIRECTORY/publish"
APP_DIRECTORY="$WORK_DIRECTORY/AsterDock.app"
DMG_PATH="$OUTPUT_DIRECTORY/AsterDock-$RID.dmg"

case "$RID" in
  osx-x64|osx-arm64) ;;
  *) echo "Unsupported runtime identifier: $RID" >&2; exit 2 ;;
esac

cleanup() {
  rm -rf "$WORK_DIRECTORY"
}
trap cleanup EXIT

mkdir -p "$OUTPUT_DIRECTORY" "$PUBLISH_DIRECTORY"
dotnet publish "$ROOT/src/AsterDock.Host/AsterDock.Host.csproj" \
  --configuration Release \
  --runtime "$RID" \
  --self-contained true \
  --output "$PUBLISH_DIRECTORY" \
  -p:PublishSingleFile=true \
  -p:BundleApplications=false \
  -p:Version="$VERSION"

mkdir -p "$APP_DIRECTORY/Contents/MacOS" "$APP_DIRECTORY/Contents/Resources"
cp "$ROOT/build/macos/Info.plist" "$APP_DIRECTORY/Contents/Info.plist"
cp "$ROOT/src/AsterDock.Host/Assets/Brand/AsterDock.icns" "$APP_DIRECTORY/Contents/Resources/AsterDock.icns"
plutil -replace CFBundleVersion -string "$VERSION" "$APP_DIRECTORY/Contents/Info.plist"
plutil -replace CFBundleShortVersionString -string "$VERSION" "$APP_DIRECTORY/Contents/Info.plist"
cp -R "$PUBLISH_DIRECTORY/"* "$APP_DIRECTORY/Contents/MacOS/"
chmod +x "$APP_DIRECTORY/Contents/MacOS/AsterDock.Host"

# hdiutil occasionally underestimates the temporary HFS image size for a
# self-contained single-file app on GitHub's Intel macOS runners. Reserve an
# additional 128 MiB so copying the bundle into the mounted image cannot fail
# before UDZO compression begins.
APP_SIZE_KB="$(du -sk "$APP_DIRECTORY" | awk '{print $1}')"
IMAGE_SIZE_KB=$((APP_SIZE_KB + 131072))
hdiutil create \
  -volname "AsterDock" \
  -srcfolder "$APP_DIRECTORY" \
  -size "${IMAGE_SIZE_KB}k" \
  -ov \
  -format UDZO \
  "$DMG_PATH"

echo "$DMG_PATH"
