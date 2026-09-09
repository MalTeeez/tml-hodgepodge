"""Extract a .tmod archive so its assembly can be decompiled.

  untmod.py <mod.tmod> <output-dir>

Format: "TMOD", loader version, 20-byte hash, 256-byte signature, data length, then
name, version, file count, a table of (path, uncompressed length, stored length), then
the file bodies back to back. A body is raw deflate when the two lengths differ.
"""
import os
import struct
import sys
import zlib


def read_string(handle):
    """.NET BinaryWriter string: 7-bit encoded length, then UTF-8 bytes."""
    length, shift = 0, 0
    while True:
        byte = handle.read(1)[0]
        length |= (byte & 0x7F) << shift
        if not byte & 0x80:
            break
        shift += 7
    return handle.read(length).decode("utf-8")


def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    archive, out_dir = sys.argv[1], sys.argv[2]

    with open(archive, "rb") as handle:
        if handle.read(4) != b"TMOD":
            sys.exit(f"{archive} is not a .tmod archive")
        loader_version = read_string(handle)
        handle.read(20 + 256 + 4)  # hash, signature, data length
        name, version = read_string(handle), read_string(handle)
        count = struct.unpack("<i", handle.read(4))[0]
        print(f"{name} {version} (tML {loader_version}) {count} files")

        table = [(read_string(handle), *struct.unpack("<ii", handle.read(8)))
                 for _ in range(count)]
        for path, size, stored in table:
            body = handle.read(stored)
            if stored != size:
                body = zlib.decompress(body, -15)  # raw deflate, no zlib header
            if len(body) != size:
                sys.exit(f"{path}: expected {size} bytes, got {len(body)}")
            target = os.path.join(out_dir, path.replace("\\", "/"))
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with open(target, "wb") as output:
                output.write(body)

    print(f"extracted to {out_dir}")


if __name__ == "__main__":
    main()
