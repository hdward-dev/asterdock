"""Build the PE icon Windows Installer uses for AsterDock shortcuts.

Advertised shortcuts do not display the icon embedded in AsterDock.Host.exe.
Windows Installer copies the Icon-table blob to
%SystemRoot%\\Installer\\{ProductCode}\\AsterDock.exe and the shell extracts
the shortcut icon from that file. Two constraints make a renamed .ico fail:

* The Icon id has to end in .exe so it matches the shortcut target. ICE50
  rejects any other extension, and the shell then treats the file as a PE.
* AsterDock.ico stores PNG images. A PNG .ico renamed to .exe is not a PE,
  so extraction returns the blank page icon.

This writes a tiny PE32 whose RT_GROUP_ICON resource is that .ico. The shell
understands PNG icon resources inside a PE, which is the same layout windres
produces.
"""

import argparse
import struct
import sys
from pathlib import Path


RT_ICON = 3
RT_GROUP_ICON = 14
ENGLISH_US = 0x0409
FILE_ALIGNMENT = 0x200
SECTION_ALIGNMENT = 0x1000
TEXT_RVA = 0x1000
RSRC_RVA = 0x2000


def _align(value: int, boundary: int) -> int:
    return (value + boundary - 1) & ~(boundary - 1)


def read_ico(data: bytes) -> list[dict]:
    if len(data) < 6:
        raise ValueError("Icon file is truncated")
    reserved, kind, count = struct.unpack_from("<HHH", data, 0)
    if reserved != 0 or kind != 1 or count <= 0:
        raise ValueError("Icon file is not a Windows .ico")
    directory_bytes = 6 + 16 * count
    if len(data) < directory_bytes:
        raise ValueError("Icon directory is truncated")
    images = []
    for index in range(count):
        offset = 6 + 16 * index
        width, height, colors, reserved_byte, planes, bit_count, size, image_offset = struct.unpack_from(
            "<BBBBHHII", data, offset
        )
        if size <= 0 or image_offset + size > len(data):
            raise ValueError(f"Icon image {index} is truncated")
        # PNG-compressed icons often leave planes at 0. The resource compiler
        # rewrites that to 1 color plane; ExtractIcon expects the same.
        if planes == 0:
            planes = 1
        if bit_count == 0:
            bit_count = 32
        images.append(
            {
                "width": width,
                "height": height,
                "colors": colors,
                "reserved": reserved_byte,
                "planes": planes,
                "bit_count": bit_count,
                "image": data[image_offset : image_offset + size],
            }
        )
    return images


def _group_icon(images: list[dict]) -> bytes:
    blob = bytearray(struct.pack("<HHH", 0, 1, len(images)))
    for index, image in enumerate(images, start=1):
        blob += struct.pack(
            "<BBBBHHIH",
            image["width"],
            image["height"],
            image["colors"],
            image["reserved"],
            image["planes"],
            image["bit_count"],
            len(image["image"]),
            index,
        )
    return bytes(blob)


def _write_directory(buffer: bytearray, at: int, entries: list[tuple[int, int, bool]]) -> None:
    struct.pack_into("<IIHHHH", buffer, at, 0, 0, 0, 0, 0, len(entries))
    cursor = at + 16
    for identifier, target, subdirectory in entries:
        offset = target | (0x80000000 if subdirectory else 0)
        struct.pack_into("<II", buffer, cursor, identifier, offset)
        cursor += 8


def _resource_section(images: list[dict]) -> bytes:
    group = _group_icon(images)
    count = len(images)
    root_size = 16 + 8 * 2
    icon_type_at = root_size
    icon_type_size = 16 + 8 * count
    icon_lang_at = icon_type_at + icon_type_size
    icon_lang_size = 24
    group_type_at = icon_lang_at + icon_lang_size * count
    group_type_size = 24
    group_lang_at = group_type_at + group_type_size
    group_lang_size = 24
    data_at = group_lang_at + group_lang_size
    payload_at = _align(data_at + 16 * (count + 1), 8)

    payloads: list[tuple[int, bytes]] = []
    cursor = payload_at
    for image in images:
        cursor = _align(cursor, 8)
        payloads.append((cursor, image["image"]))
        cursor += len(image["image"])
    cursor = _align(cursor, 8)
    group_at = cursor
    section = bytearray(cursor + len(group))

    _write_directory(
        section,
        0,
        [(RT_ICON, icon_type_at, True), (RT_GROUP_ICON, group_type_at, True)],
    )
    _write_directory(
        section,
        icon_type_at,
        [(index + 1, icon_lang_at + index * icon_lang_size, True) for index in range(count)],
    )
    for index in range(count):
        _write_directory(
            section,
            icon_lang_at + index * icon_lang_size,
            [(ENGLISH_US, data_at + index * 16, False)],
        )
    _write_directory(section, group_type_at, [(1, group_lang_at, True)])
    _write_directory(section, group_lang_at, [(ENGLISH_US, data_at + count * 16, False)])

    for index, (at, blob) in enumerate(payloads):
        struct.pack_into("<IIII", section, data_at + index * 16, RSRC_RVA + at, len(blob), 0, 0)
        section[at : at + len(blob)] = blob
    struct.pack_into("<IIII", section, data_at + count * 16, RSRC_RVA + group_at, len(group), 0, 0)
    section[group_at : group_at + len(group)] = group
    return bytes(section)


def _section_header(
    name: str, virtual_size: int, virtual_address: int, raw_size: int, raw_pointer: int, characteristics: int
) -> bytes:
    return struct.pack(
        "<8sIIIIIIHHI",
        name.encode("ascii").ljust(8, b"\x00"),
        virtual_size,
        virtual_address,
        raw_size,
        raw_pointer,
        0,
        0,
        0,
        0,
        characteristics,
    )


def build_icon_executable(ico: bytes) -> bytes:
    images = read_ico(ico)
    resource = _resource_section(images)
    text = b"\x33\xC0\xC3"
    text_raw = text.ljust(FILE_ALIGNMENT, b"\x00")
    resource_raw = resource.ljust(_align(len(resource), FILE_ALIGNMENT), b"\x00")
    size_of_headers = FILE_ALIGNMENT
    text_raw_pointer = size_of_headers
    resource_raw_pointer = text_raw_pointer + len(text_raw)
    size_of_image = RSRC_RVA + _align(len(resource), SECTION_ALIGNMENT)

    optional = struct.pack(
        "<HBBIIIIIIIIIHHHHHHIIIIHHIIIIII",
        0x10B,
        14,
        0,
        len(text_raw),
        len(resource_raw),
        0,
        TEXT_RVA,
        TEXT_RVA,
        RSRC_RVA,
        0x00400000,
        SECTION_ALIGNMENT,
        FILE_ALIGNMENT,
        6,
        0,
        0,
        0,
        6,
        0,
        0,
        size_of_image,
        size_of_headers,
        0,
        2,
        0x0100,
        0x100000,
        0x1000,
        0x100000,
        0x1000,
        0,
        16,
    )
    if len(optional) != 96:
        raise RuntimeError(f"PE optional header is {len(optional)} bytes, expected 96")
    directories = bytearray(16 * 8)
    struct.pack_into("<II", directories, 2 * 8, RSRC_RVA, len(resource))
    file_header = struct.pack("<HHIIIHH", 0x14C, 2, 0, 0, 0, len(optional) + len(directories), 0x0102)
    if len(file_header) != 20:
        raise RuntimeError(f"PE file header is {len(file_header)} bytes, expected 20")
    sections = _section_header(".text", len(text_raw), TEXT_RVA, len(text_raw), text_raw_pointer, 0x60000020)
    sections += _section_header(
        ".rsrc", len(resource), RSRC_RVA, len(resource_raw), resource_raw_pointer, 0x40000040
    )
    dos = bytearray(0x40)
    dos[0:2] = b"MZ"
    struct.pack_into("<I", dos, 0x3C, 0x40)
    headers = bytes(dos) + b"PE\0\0" + file_header + optional + bytes(directories) + sections
    if len(headers) > size_of_headers:
        raise RuntimeError("PE headers do not fit in the header alignment")
    return headers.ljust(size_of_headers, b"\x00") + text_raw + resource_raw


def _u16(blob: bytes, offset: int) -> int:
    return struct.unpack_from("<H", blob, offset)[0]


def _u32(blob: bytes, offset: int) -> int:
    return struct.unpack_from("<I", blob, offset)[0]


def _rva_to_offset(pe: bytes, sections: list[tuple[int, int, int, int]], rva: int) -> int:
    for virtual_address, virtual_size, raw_pointer, raw_size in sections:
        span = max(virtual_size, raw_size)
        if virtual_address <= rva < virtual_address + span:
            return raw_pointer + (rva - virtual_address)
    raise ValueError(f"RVA {rva:#x} is outside the PE sections")


def _read_resource_directory(pe: bytes, offset: int) -> list[tuple[int, int, bool]]:
    named = _u16(pe, offset + 12)
    identifiers = _u16(pe, offset + 14)
    entries = []
    cursor = offset + 16
    for _ in range(named + identifiers):
        identifier = _u32(pe, cursor)
        value = _u32(pe, cursor + 4)
        entries.append((identifier, value & 0x7FFFFFFF, bool(value & 0x80000000)))
        cursor += 8
    return entries


def _resource_blobs(pe: bytes, resource_type: int) -> list[tuple[int, bytes]]:
    if pe[:2] != b"MZ" or pe[_u32(pe, 0x3C) : _u32(pe, 0x3C) + 4] != b"PE\0\0":
        raise ValueError("Icon container is not a PE")
    pe_offset = _u32(pe, 0x3C)
    section_count = _u16(pe, pe_offset + 6)
    optional_size = _u16(pe, pe_offset + 20)
    optional = pe_offset + 24
    if _u16(pe, optional) != 0x10B:
        raise ValueError("Icon container is not PE32")
    resource_rva = _u32(pe, optional + 96 + 16)
    section_table = optional + optional_size
    sections = []
    for index in range(section_count):
        entry = section_table + index * 40
        sections.append((_u32(pe, entry + 12), _u32(pe, entry + 8), _u32(pe, entry + 20), _u32(pe, entry + 16)))
    resource_offset = _rva_to_offset(pe, sections, resource_rva)
    found = []
    for identifier, target, subdirectory in _read_resource_directory(pe, resource_offset):
        if identifier != resource_type or not subdirectory:
            continue
        for name, language_target, language_directory in _read_resource_directory(pe, resource_offset + target):
            if not language_directory:
                continue
            languages = _read_resource_directory(pe, resource_offset + language_target)
            if len(languages) != 1 or languages[0][2]:
                raise ValueError("Icon resource language entry is invalid")
            data_entry = resource_offset + languages[0][1]
            data_rva = _u32(pe, data_entry)
            size = _u32(pe, data_entry + 4)
            data_offset = _rva_to_offset(pe, sections, data_rva)
            found.append((name, pe[data_offset : data_offset + size]))
    found.sort(key=lambda item: item[0])
    return found


def icon_images(pe: bytes) -> list[bytes]:
    return [blob for _, blob in _resource_blobs(pe, RT_ICON)]


def group_icon_directory(pe: bytes) -> list[dict]:
    groups = _resource_blobs(pe, RT_GROUP_ICON)
    if len(groups) != 1:
        raise ValueError(f"Expected one icon group, found {len(groups)}")
    blob = groups[0][1]
    reserved, kind, count = struct.unpack_from("<HHH", blob, 0)
    if reserved != 0 or kind != 1 or 6 + 14 * count != len(blob):
        raise ValueError("Icon group resource is invalid")
    entries = []
    for index in range(count):
        offset = 6 + 14 * index
        width, height, colors, reserved_byte, planes, bit_count, size, resource_id = struct.unpack_from(
            "<BBBBHHIH", blob, offset
        )
        entries.append(
            {
                "width": width,
                "height": height,
                "colors": colors,
                "reserved": reserved_byte,
                "planes": planes,
                "bit_count": bit_count,
                "size": size,
                "id": resource_id,
            }
        )
    return entries


def main() -> int:
    parser = argparse.ArgumentParser(description="Build a PE shortcut icon from a Windows .ico file.")
    parser.add_argument("ico", type=Path, help="Source .ico, typically AsterDock.ico")
    parser.add_argument("exe", type=Path, help="Output PE consumed by the WiX Icon element")
    args = parser.parse_args()
    try:
        executable = build_icon_executable(args.ico.read_bytes())
    except (OSError, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    args.exe.parent.mkdir(parents=True, exist_ok=True)
    args.exe.write_bytes(executable)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
