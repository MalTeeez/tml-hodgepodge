"""Print the IL, or the decompiled C#, of one type or method from an installed mod.

  dump_il.py <Mod> <Namespace.Type>[/<Nested>][::<Method>]
  dump_il.py --csharp <Mod> <Namespace.Type>[/<Nested>]

Every patch here pins the shape of the IL it rewrites -- the constant before an AnyNPCs, the
ldstr before a Find, one Begin and one End around one Draw -- and this is how that shape is read
before the patch is written and read again after the mod updates. The C# view is the one the
patch comments quote; the IL view is the one the manipulator matches.

The newest <Mod>.tmod in the workshop and Mods folders is extracted and its assembly dumped once
per version into a cache under the temp directory. That is the slow part: ilspycmd's -t filter
does not apply to -il, so the IL of one method costs a dump of the whole assembly, which for
Calamity is 180 MB and about three minutes. Later calls slice the cached dump in a second.

Set TMODLOADER_DIR when tModLoader is not in the default Steam library, and
TMODLOADER_WORKSHOP when its workshop content is not in the same library.
"""
import argparse
import glob
import os
import pathlib
import re
import struct
import subprocess
import sys
import tempfile

sys.path.insert(0, os.path.dirname(__file__))
from untmod import read_string  # noqa: E402

TML_DIR = os.environ.get("TMODLOADER_DIR",
                         r"C:\Program Files (x86)\Steam\steamapps\common\tModLoader")
# Steam keeps a library's workshop content beside its common folder, so the library that holds
# tModLoader holds its mods; TMODLOADER_WORKSHOP covers a setup where they live apart.
WORKSHOP = os.environ.get("TMODLOADER_WORKSHOP", os.path.join(
    os.path.dirname(os.path.dirname(TML_DIR)), "workshop", "content", "1281930"))
MODS = os.path.expanduser(r"~\Documents\My Games\Terraria\tModLoader\Mods")
CACHE = pathlib.Path(tempfile.gettempdir()) / "hodgepodge" / "il"


def header(tmod):
    """(name, version) from a .tmod header, without reading the archive body."""
    with open(tmod, "rb") as handle:
        if handle.read(4) != b"TMOD":
            return None, None
        read_string(handle)  # loader version
        handle.read(20 + 256 + 4)  # hash, signature, data length
        return read_string(handle), read_string(handle)


def newest_tmod(mod):
    """The highest version of <mod>.tmod across the workshop and the Mods folder."""
    candidates = glob.glob(os.path.join(WORKSHOP, "*", "*", f"{mod}.tmod"))
    candidates += glob.glob(os.path.join(MODS, f"{mod}.tmod"))
    found = []
    for tmod in candidates:
        name, version = header(tmod)
        if name == mod:
            found.append((tuple(int(part) for part in version.split(".")), version, tmod))
    if not found:
        sys.exit(f"no {mod}.tmod under {WORKSHOP} or {MODS}")
    return max(found)[1:]


def extracted(mod):
    """Directory holding the mod's extracted files, extracting on first use."""
    version, tmod = newest_tmod(mod)
    out_dir = CACHE / f"{mod}-{version}"
    dll = out_dir / f"{mod}.dll"
    if not dll.exists():
        subprocess.run([sys.executable, os.path.join(os.path.dirname(__file__), "untmod.py"),
                        tmod, str(out_dir)], check=True, stdout=subprocess.DEVNULL)
    return out_dir, dll


def il_dump(mod):
    """Path of the mod's whole-assembly IL dump, produced on first use."""
    out_dir, dll = extracted(mod)
    dump = out_dir / f"{mod}.il"
    if not dump.exists():
        print(f"dumping {dll.name}, this takes a while for a large mod", file=sys.stderr)
        with open(dump, "wb") as output:
            subprocess.run(["ilspycmd", "-il", "-r", TML_DIR, str(dll)], check=True,
                           stdout=output)
    return dump


def block(lines, start):
    """Lines from `start` through the closing brace of the block opened after it."""
    depth = 0
    for index in range(start, len(lines)):
        stripped = lines[index].strip()
        if stripped.startswith("{"):
            depth += 1
        elif stripped.startswith("}"):
            depth -= 1
            if depth == 0:
                return lines[start:index + 1]
    return lines[start:]


def find_type(lines, path):
    """The `.class` block for Outer/Nested, searching each nested name inside the last."""
    outer, *nested = path.split("/")
    pattern = re.compile(rf"^\.class .*\b{re.escape(outer)}$")
    start = next((index for index, line in enumerate(lines) if pattern.match(line)), None)
    if start is None:
        sys.exit(f"no type {outer} in the dump")
    found = block(lines, start)
    for name in nested:
        pattern = re.compile(rf"^\s*\.class nested .*\b{re.escape(name)}$")
        start = next((index for index, line in enumerate(found) if pattern.match(line)), None)
        if start is None:
            sys.exit(f"no nested type {name} in {outer}")
        found = block(found, start)
    return found


def find_method(lines, name):
    """Every `.method` block with that name, since overloads share one.

    The header spans lines -- `.method public hidebysig` on one, the return type and name with
    the opening parenthesis on the next -- so the name is matched on its own line and the block
    starts at the `.method` line before it.
    """
    pattern = re.compile(rf"^\s*\S.*\s{re.escape(name)} \(")
    found = []
    header = None
    for index, line in enumerate(lines):
        if re.match(r"^\s*\.method\b", line):
            header = index
        elif header is not None and pattern.match(line):
            found.extend(block(lines, header))
            found.append("")
            header = None
    if not found:
        sys.exit(f"no method {name} in the type")
    return found


def csharp(mod, type_path):
    """Decompile one type, resolving references against tModLoader and every cached mod."""
    _, dll = extracted(mod)
    refs = ["-r", TML_DIR]
    for other in CACHE.iterdir():
        if other.is_dir() and other != dll.parent:
            refs += ["-r", str(other)]
    subprocess.run(["ilspycmd", "-t", type_path.replace("/", "+"), *refs, str(dll)], check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[1],
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("mod", help="mod name as tModLoader knows it, e.g. CalamityMod")
    parser.add_argument("target", help="Namespace.Type[/Nested][::Method]")
    parser.add_argument("--csharp", "-c", action="store_true",
                        help="decompile the type to C# instead of printing its IL")
    args = parser.parse_args()

    type_path, _, method = args.target.partition("::")
    if args.csharp:
        csharp(args.mod, type_path)
        return

    # ilspycmd writes string literals in the console's encoding, and a literal is never what a
    # patch matches on, so an undecodable byte is replaced rather than allowed to fail the read.
    with open(il_dump(args.mod), encoding="utf-8", errors="replace") as dump:
        lines = dump.read().splitlines()
    found = find_type(lines, type_path)
    if method:
        found = find_method(found, method)
    sys.stdout.write("\n".join(found) + "\n")


if __name__ == "__main__":
    main()
