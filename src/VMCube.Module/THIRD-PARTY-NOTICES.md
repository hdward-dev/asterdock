# VMCUBE native components

VMCUBE reuses the repository's URemote managed RFB/VNC transport and key mapping.

The isolated SPICE worker dynamically links to spice-client-glib from spice-gtk
(https://www.spice-space.org/), primarily licensed LGPL-2.1-or-later. Sources and
build instructions for the worker are in src/VMCube.Module/Native and
scripts/build-vmcube-native.sh. Upstream source releases:
https://www.spice-space.org/download/gtk/ . The bundled worker does not use GTK UI.

Optional QEMU engine packages contain QEMU (GPL-2.0) and its runtime dependencies.
QEMU sources: https://download.qemu.org/ ; build documentation:
https://www.qemu.org/docs/master/devel/build-system.html . BIOS firmware comes
from the QEMU distribution (including SeaBIOS/VGABIOS); firmware notices are
included where supplied upstream. No guest operating system or install ISO is
included.

Native shared dependencies remain separately loadable. native-dependencies.json
records packaged file provenance and versions where available in source paths.
The build script preserves distribution copyright files when available.
GLib (LGPL-2.1-or-later), GStreamer (LGPL-2.0-or-later), OpenSSL (Apache-2.0),
libjpeg-turbo (IJG/BSD/Zlib), pixman (MIT), and other dependencies retain their
respective upstream licenses. Bundled libraries can be replaced with compatible
builds; no integrity restriction prevents LGPL relinking.

This local build was produced with Nix; official CI produces binaries against
its target runner platform. Windows/macOS binaries need validation on their
respective target systems before release claims are made.
