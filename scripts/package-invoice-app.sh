#!/usr/bin/env bash
set -euo pipefail

CONFIGURATION="${1:-Release}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SOURCE="$ROOT/src/InvoicePrinter.Module/bin/$CONFIGURATION/net10.0"
OUTPUT="$ROOT/artifacts/apps/InvoicePrinter.appbundle"

dotnet build "$ROOT/src/InvoicePrinter.Module/InvoicePrinter.Module.csproj" -c "$CONFIGURATION"
mkdir -p "$(dirname "$OUTPUT")"
rm -f "$OUTPUT"
(cd "$SOURCE" && zip -qr "$OUTPUT" .)
echo "$OUTPUT"
