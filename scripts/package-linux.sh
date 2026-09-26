#!/usr/bin/env bash
# Publishes a self-contained Linux build and packs it as a per-user tarball.
#
#   bash scripts/package-linux.sh [linux-x64|linux-arm64] [version] [output-directory]
set -euo pipefail

# Packing on macOS would otherwise add AppleDouble sidecars to the tarball.
export COPYFILE_DISABLE=1

RID="${1:-linux-x64}"
VERSION="${2:-1.0.0}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUTPUT_DIRECTORY="${3:-$ROOT/artifacts/release/$RID}"
WORK_DIRECTORY="$(mktemp -d)"
PUBLISH_DIRECTORY="$WORK_DIRECTORY/publish"
STAGE_DIRECTORY="$WORK_DIRECTORY/AsterDock-$RID"
TARBALL_PATH="$OUTPUT_DIRECTORY/AsterDock-$RID.tar.gz"
BRAND_ASSETS="$ROOT/src/AsterDock.Host/Assets/Brand"

case "$RID" in
  linux-x64|linux-arm64) ;;
  *) echo "Unsupported runtime identifier: $RID" >&2; exit 2 ;;
esac

cleanup() {
  rm -rf "$WORK_DIRECTORY"
}
trap cleanup EXIT

mkdir -p "$OUTPUT_DIRECTORY" "$PUBLISH_DIRECTORY" "$STAGE_DIRECTORY"
dotnet publish "$ROOT/src/AsterDock.Host/AsterDock.Host.csproj" \
  --configuration Release \
  --runtime "$RID" \
  --self-contained true \
  --output "$PUBLISH_DIRECTORY" \
  -p:PublishSingleFile=true \
  -p:BundleApplications=false \
  -p:Version="$VERSION"

cp -R "$PUBLISH_DIRECTORY/." "$STAGE_DIRECTORY/"
chmod +x "$STAGE_DIRECTORY/AsterDock.Host"

# The release has to know its own version: install.sh derives the install
# directory from it, and the in-app updater writes to the same layout.
printf '%s\n' "$VERSION" > "$STAGE_DIRECTORY/AsterDock-VERSION"
cp "$ROOT/build/linux/AsterDock.desktop" "$STAGE_DIRECTORY/AsterDock.desktop"
cp "$BRAND_ASSETS/AsterDock.svg" "$STAGE_DIRECTORY/AsterDock.svg"
cp "$BRAND_ASSETS/AsterDock.png" "$STAGE_DIRECTORY/AsterDock.png"
sed 's/\r$//' "$ROOT/build/linux/install.sh" > "$STAGE_DIRECTORY/install.sh"
sed 's/\r$//' "$ROOT/build/linux/uninstall.sh" > "$STAGE_DIRECTORY/uninstall.sh"
chmod +x "$STAGE_DIRECTORY/install.sh" "$STAGE_DIRECTORY/uninstall.sh"

rm -f "$TARBALL_PATH"
tar -czf "$TARBALL_PATH" -C "$WORK_DIRECTORY" "AsterDock-$RID"

echo "$TARBALL_PATH"
