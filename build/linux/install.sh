#!/usr/bin/env bash
# Installs AsterDock from an unpacked release into the per-user prefix. No root
# access is required and nothing outside $HOME is touched.
#
#   ./install.sh              install or upgrade the current user session
#   ./install.sh --uninstall  remove the launcher, the icons and the install root
set -euo pipefail

SOURCE_DIRECTORY="$(cd "$(dirname "$0")" && pwd)"
DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
CONFIG_HOME="${XDG_CONFIG_HOME:-$HOME/.config}"
INSTALL_PARENT="${ASTERDOCK_INSTALL_PARENT:-$HOME/.local/opt/asterdock}"
VERSION="$(tr -d '[:space:]' < "$SOURCE_DIRECTORY/AsterDock-VERSION" 2>/dev/null || true)"
if [[ -z "$VERSION" ]]; then
  echo "Cannot read AsterDock-VERSION next to install.sh" >&2
  exit 2
fi

INSTALL_DIRECTORY="$INSTALL_PARENT/$VERSION"
APPLICATIONS_DIRECTORY="$DATA_HOME/applications"
LAUNCHER="$APPLICATIONS_DIRECTORY/AsterDock.desktop"
EXECUTABLE="$INSTALL_DIRECTORY/AsterDock.Host"
UNINSTALL=0
[[ "${1:-}" == "--uninstall" ]] && UNINSTALL=1

if [[ "$UNINSTALL" == "1" ]]; then
  rm -f "$LAUNCHER"
  rm -f "$DATA_HOME/icons/hicolor/scalable/apps/AsterDock.svg"
  rm -f "$DATA_HOME/icons/hicolor/1024x1024/apps/AsterDock.png"
  rm -rf "$INSTALL_PARENT"
  command -v update-desktop-database >/dev/null 2>&1 &&
    update-desktop-database "$APPLICATIONS_DIRECTORY" >/dev/null 2>&1 || true
  echo "AsterDock has been removed. User data stays in $DATA_HOME/AsterDock."
  exit 0
fi

if [[ ! -f "$SOURCE_DIRECTORY/AsterDock.Host" ]]; then
  echo "AsterDock.Host is missing; unpack the whole release before installing" >&2
  exit 2
fi

# Replace the previous build atomically enough for a per-user install: the new
# tree is staged next to the target and swapped in once it is complete.
STAGED_DIRECTORY="$INSTALL_PARENT/.staging-$VERSION"
rm -rf "$STAGED_DIRECTORY"
mkdir -p "$STAGED_DIRECTORY"
trap 'rm -rf "$STAGED_DIRECTORY"' EXIT
for entry in "$SOURCE_DIRECTORY"/*; do
  name="$(basename "$entry")"
  case "$name" in
    install.sh|uninstall.sh|AsterDock-VERSION) continue ;;
  esac
  cp -R "$entry" "$STAGED_DIRECTORY/"
done
chmod +x "$STAGED_DIRECTORY/AsterDock.Host"
rm -rf "$INSTALL_DIRECTORY"
mkdir -p "$INSTALL_PARENT"
mv "$STAGED_DIRECTORY" "$INSTALL_DIRECTORY"
trap - EXIT

mkdir -p "$APPLICATIONS_DIRECTORY" \
  "$DATA_HOME/icons/hicolor/scalable/apps" \
  "$DATA_HOME/icons/hicolor/1024x1024/apps" \
  "$CONFIG_HOME/autostart"

# The template ships a relative Exec so it stays reviewable; the installed copy
# needs absolute paths, and the in-app updater rewrites these same lines.
sed -e "s|^Exec=.*|Exec=$EXECUTABLE|" \
    -e "s|^TryExec=.*|TryExec=$EXECUTABLE|" \
    "$SOURCE_DIRECTORY/AsterDock.desktop" > "$LAUNCHER"
chmod +x "$LAUNCHER"
cp "$SOURCE_DIRECTORY/AsterDock.svg" "$DATA_HOME/icons/hicolor/scalable/apps/AsterDock.svg"
cp "$SOURCE_DIRECTORY/AsterDock.png" "$DATA_HOME/icons/hicolor/1024x1024/apps/AsterDock.png"

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$APPLICATIONS_DIRECTORY" >/dev/null 2>&1 || true
fi

cat <<EOF
AsterDock $VERSION installed to $INSTALL_DIRECTORY

Launch it from the application menu, or run:
  $EXECUTABLE

To start it automatically with your desktop session:
  ln -s $LAUNCHER $CONFIG_HOME/autostart/AsterDock.desktop

To remove it:
  $SOURCE_DIRECTORY/install.sh --uninstall
EOF
