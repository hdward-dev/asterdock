# Android screen checks

Run protocol, address validation, frame bounds, device-state parsing, and coordinate tests:

```sh
dotnet run --project tests/AndroidScreen.Checks
```

Run the local simulated device through the real packaged H.264 decoder (macOS with Python 3):

```sh
dotnet run --project tests/AndroidScreen.Checks -- --integration
```

The fixture does not use a real ADB server or Android device. It uses loopback sockets, fragmented handshakes/packet headers, recorded input commands, clipboard responses, and two video resolutions. It checks encoder restart on rotation, disconnect reporting, port-forward removal, and server-file cleanup.

`Fixtures/landscape.h264` and `portrait.h264` are synthetic FFmpeg `testsrc2` patterns, generated locally with `-f lavfi -i testsrc2=size=320x180:rate=15 -t 1 -c:v h264_videotoolbox -allow_sw 1 -bf 0 -g 15 -f h264` (swap dimensions for portrait). They contain no device or personal data.

Before release, additionally test real USB authorization, Android 11+ pairing, Chinese input/clipboard, rotation during dragging, cancellation during download/handshake, device unplug/reconnect, and Windows/macOS packaged builds. Audio stays on the phone; this release only carries video and controls.

Optional network smoke test for the pinned official release installer:

```sh
dotnet run --project tests/AndroidScreen.Checks -- --install-smoke
```

This downloads to a unique temporary directory, checks extraction and core executability, then deletes the test installation.

The portrait fixture additionally carries YCgCo/log316 color metadata, reproduced with `-c:v copy -bsf:v h264_metadata=colour_primaries=2:transfer_characteristics=10:matrix_coefficients=8`. This models the NTH-AN00 encoder metadata that caused FFmpeg 8 RGB conversion to fail. The integration test must still receive the portrait bitmap after encoder restart.
