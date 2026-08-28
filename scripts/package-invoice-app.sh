#!/usr/bin/env bash
set -euo pipefail

CONFIGURATION="${1:-Release}"
VERSION="${2:-1.0.0}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SOURCE="$ROOT/src/InvoicePrinter.Module/bin/$CONFIGURATION/net10.0"
OUTPUT="$ROOT/artifacts/apps/AsterDockApp-invoice-printer-$VERSION.appbundle"

dotnet build "$ROOT/src/InvoicePrinter.Module/InvoicePrinter.Module.csproj" -c "$CONFIGURATION"
MANIFEST_VERSION="$(sed -n 's/^[[:space:]]*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$SOURCE/app.json")"
if [[ "$MANIFEST_VERSION" != "$VERSION" ]]; then
  echo "Version $VERSION does not match app.json version $MANIFEST_VERSION" >&2
  exit 2
fi
mkdir -p "$(dirname "$OUTPUT")"
rm -f "$OUTPUT"
(cd "$SOURCE" && zip -qr "$OUTPUT" .)
echo "$OUTPUT"
