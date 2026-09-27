#!/usr/bin/env bash
# Convenience wrapper so the release can be unpacked and reverted without
# remembering the flag. See install.sh for the real work.
set -euo pipefail
exec "$(cd "$(dirname "$0")" && pwd)/install.sh" --uninstall "$@"
