#!/usr/bin/env bash
set -euo pipefail
RID="${1:-linux-x64}"
VERSION="${2:-1.0.0}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUTPUT="${3:-$ROOT/artifacts/release/$RID}"
case "$RID" in
  linux-x64) DEB_ARCH=amd64; RPM_ARCH=x86_64 ;;
  linux-arm64) DEB_ARCH=arm64; RPM_ARCH=aarch64 ;;
  *) echo "Unsupported runtime: $RID" >&2; exit 2 ;;
esac
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'Invalid version' >&2; exit 2; }
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
STAGE="$WORK/stage"
mkdir -p "$OUTPUT" "$STAGE/opt/asterdock" "$STAGE/usr/bin" "$STAGE/usr/share/applications" "$STAGE/usr/share/icons/hicolor/scalable/apps"
dotnet publish "$ROOT/src/AsterDock.Host/AsterDock.Host.csproj" -c Release -r "$RID" \
  --self-contained true -o "$STAGE/opt/asterdock" -p:PublishSingleFile=false \
  -p:BundleApplications=false -p:Version="$VERSION"
chmod 755 "$STAGE/opt/asterdock/AsterDock.Host"
cat > "$STAGE/usr/bin/asterdock" <<'LAUNCH'
#!/bin/sh
exec /opt/asterdock/AsterDock.Host "$@"
LAUNCH
chmod 755 "$STAGE/usr/bin/asterdock"
cat > "$STAGE/usr/share/applications/asterdock.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=AsterDock
Name[zh_CN]=星栈
Comment=Lightweight application host
Exec=asterdock
Icon=asterdock
Terminal=false
Categories=Utility;
StartupWMClass=AsterDock.Host
DESKTOP
cp "$ROOT/src/AsterDock.Host/Assets/Brand/AsterDock.svg" "$STAGE/usr/share/icons/hicolor/scalable/apps/asterdock.svg"
# Build RPM before adding Debian-only metadata to the staging tree.
mkdir -p "$WORK/rpm/"{BUILD,BUILDROOT,RPMS,SOURCES,SPECS,SRPMS}
cat > "$WORK/rpm/SPECS/asterdock.spec" <<SPEC
Name: asterdock
Version: $VERSION
Release: 1
Summary: AsterDock lightweight application host
License: LicenseRef-Unspecified
URL: https://github.com/hdward-dev/asterdock
BuildArch: $RPM_ARCH
AutoReqProv: no
Requires: glibc, libgcc, libstdc++, zlib, openssl-libs, libicu, fontconfig, libX11, libICE, libSM, libxcb, libxkbcommon
%description
AsterDock desktop application host with its built-in home page.
Install optional lightweight applications through the application catalog.
%install
mkdir -p %{buildroot}
cp -a $STAGE/. %{buildroot}/
%files
/opt/asterdock
/usr/bin/asterdock
/usr/share/applications/asterdock.desktop
/usr/share/icons/hicolor/scalable/apps/asterdock.svg
SPEC
rpmbuild --define "_topdir $WORK/rpm" --define '_build_id_links none' \
  --define '__os_install_post %{nil}' --target "$RPM_ARCH" -bb "$WORK/rpm/SPECS/asterdock.spec"
cp "$WORK/rpm/RPMS/$RPM_ARCH/asterdock-$VERSION-1.$RPM_ARCH.rpm" "$OUTPUT/AsterDock-$RID.rpm"
mkdir -p "$STAGE/DEBIAN"
cat > "$STAGE/DEBIAN/control" <<CONTROL
Package: asterdock
Version: $VERSION
Section: utils
Priority: optional
Architecture: $DEB_ARCH
Maintainer: hdward-dev <83993897+hdward-dev@users.noreply.github.com>
Homepage: https://github.com/hdward-dev/asterdock
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libssl3 | libssl3t64, libicu74 | libicu76 | libicu78 | libicu72, libfontconfig1, libx11-6, libice6, libsm6, libxcb1, libxkbcommon0
Description: AsterDock lightweight application host
 Desktop host with a built-in home page and optional application catalog.
CONTROL
chmod 755 "$STAGE/DEBIAN"
chmod 644 "$STAGE/DEBIAN/control"
dpkg-deb --root-owner-group --build "$STAGE" "$OUTPUT/AsterDock-$RID.deb"
dpkg-deb --info "$OUTPUT/AsterDock-$RID.deb"
rpm -qip "$OUTPUT/AsterDock-$RID.rpm"
(cd "$OUTPUT" && sha256sum "AsterDock-$RID.deb" "AsterDock-$RID.rpm" > "SHA256SUMS-$RID.txt")
