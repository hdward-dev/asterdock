#!/usr/bin/env bash
set -euo pipefail

CONFIGURATION="${1:-Release}"
VERSION="${2:-1.0.0}"
RUNTIME_IDENTIFIER="${3:?Usage: $0 [configuration] [version] <win-x64|win-arm64|osx-x64|osx-arm64>}"

case "$RUNTIME_IDENTIFIER" in
  win-x64|win-arm64|osx-x64|osx-arm64) ;;
  *)
    echo "Unsupported runtime identifier: $RUNTIME_IDENTIFIER" >&2
    exit 2
    ;;
esac

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SOURCE="$ROOT/src/InvoicePrinter.Module/bin/$CONFIGURATION/net10.0/$RUNTIME_IDENTIFIER"
OUTPUT="$ROOT/artifacts/apps/AsterDockApp-invoice-printer-$VERSION-$RUNTIME_IDENTIFIER.appbundle"
STAGING="$ROOT/artifacts/apps/.invoice-printer-$VERSION-$RUNTIME_IDENTIFIER"

dotnet build "$ROOT/src/InvoicePrinter.Module/InvoicePrinter.Module.csproj" -c "$CONFIGURATION" --runtime "$RUNTIME_IDENTIFIER" --self-contained false
MANIFEST_VERSION="$(sed -n 's/^[[:space:]]*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$SOURCE/app.json")"
if [[ "$MANIFEST_VERSION" != "$VERSION" ]]; then
  echo "Version $VERSION does not match app.json version $MANIFEST_VERSION" >&2
  exit 2
fi
mkdir -p "$(dirname "$OUTPUT")"
rm -f "$OUTPUT"
rm -rf "$STAGING"
mkdir -p "$STAGING"
trap 'rm -rf "$STAGING"' EXIT
cp -R "$SOURCE"/. "$STAGING"

# Host assemblies and Avalonia/Skia/HarfBuzz natives are supplied by AsterDock.
# Do not delete runtimes/: a RID build may still place PDFium under
# runtimes/<rid>/native, and the flattened libpdfium/pdfium file must stay.
for pattern in AsterDock.Contracts.dll AsterDock.UI.dll 'Avalonia*.dll' HarfBuzzSharp.dll 'Irihi.*.dll' MicroCom.Runtime.dll 'Semi.*.dll' SkiaSharp.dll Tmds.DBus.Protocol.dll 'Ursa*.dll'; do
  rm -f "$STAGING"/$pattern
done
find "$STAGING" -type f \( \
    -name 'libAvaloniaNative.*' -o \
    -name 'libHarfBuzzSharp.*' -o \
    -name 'libSkiaSharp.*' -o \
    -name 'av_libglesv2.*' -o \
    -name '*.pdb' \
  \) -delete
if [[ -d "$STAGING/runtimes" ]]; then
  find "$STAGING/runtimes" -depth -type d -empty -delete
fi

pdfium_name="libpdfium.so"
case "$RUNTIME_IDENTIFIER" in
  win-*) pdfium_name="pdfium.dll" ;;
  osx-*) pdfium_name="libpdfium.dylib" ;;
esac
if [[ ! -f "$STAGING/$pdfium_name" && ! -f "$STAGING/runtimes/$RUNTIME_IDENTIFIER/native/$pdfium_name" ]]; then
  echo "Invoice Printer package is missing $pdfium_name for $RUNTIME_IDENTIFIER." >&2
  exit 1
fi
if find "$STAGING" -type f \( \
    -name 'libAvaloniaNative.*' -o -name 'libHarfBuzzSharp.*' -o -name 'libSkiaSharp.*' -o \
    -name 'av_libglesv2.*' -o -name '*.pdb' -o -name 'SkiaSharp.dll' -o -name 'HarfBuzzSharp.dll' -o \
    -name 'Avalonia*.dll' \
  \) | grep -q .; then
  echo "Host-supplied assets are still in the Invoice Printer package." >&2
  exit 1
fi

(cd "$STAGING" && zip -qr "$OUTPUT" .)
echo "$OUTPUT"
