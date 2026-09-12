# Android Screen third-party components

- scrcpy 3.3.4, Copyright Genymobile and contributors, Apache License 2.0.
  Source and license: https://github.com/Genymobile/scrcpy/tree/v3.3.4
  The application downloads the unmodified official platform archive and verifies its release SHA-256 digest. The archive includes ADB and its notices. The client protocol is pinned to this release.
- FFmpeg 8.0.1, LGPL 2.1 or later, distributed as unmodified executable and shared libraries via DevEnvy.FFmpeg.Binaries.LGPL 8.0.1.4.
  Package: https://www.nuget.org/packages/DevEnvy.FFmpeg.Binaries.LGPL/8.0.1.4
  Upstream source: https://ffmpeg.org/releases/ffmpeg-8.0.1.tar.xz
  License: https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html
  See ffmpeg/THIRD_PARTY_NOTICES.md for additional components and build information.

FFmpeg runs as a separate background process. Users may replace the compatible executable and shared libraries in ffmpeg/<rid>/; this application does not prohibit modification or reverse engineering of those libraries for debugging modifications. Distributors must preserve notices and provide corresponding source and build information as required by the applicable licenses.
