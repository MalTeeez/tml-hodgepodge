"""Find code in the enabled mods that reads the held item snapshot vanilla keeps for drawing.

  scan_helditem.py

Player.lastVisualizedSelectedItem is a clone of the held item, refreshed every tick for every
player in ItemCheckWrapped and ItemCheck_Inner, and handed to draw code as PlayerDrawSet.heldItem.
The proposed patch stops refreshing it while the held item's netID, stack and prefix are unchanged,
which is the test vanilla already applies to the local player during item use. What that can
break is a mod that mutates per-instance state on the held item -- a GlobalItem field, a ModItem
field -- and reads it back off the snapshot expecting this tick's value.

Only reads of the snapshot can notice, so the risk set is every method that loads either member.
Each is listed with the calls in it that reach per-instance data: GetGlobalItem, TryGetGlobalItem,
ModItem. A method with none of those reads only Item's own fields, which the key covers except
for fields a mod writes on the item itself, so the list is what to read, not a verdict.

Mod assemblies whose metadata never names either member are skipped without an IL dump; the rest
are dumped through dump_il's cache, which is slow the first time for a large mod.
"""
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(__file__))
import dump_il  # noqa: E402

SNAPSHOT_MEMBERS = (b"lastVisualizedSelectedItem", b"heldItem")
# Address loads count: a snapshot passed by reference is read by whatever receives it.
SNAPSHOT_LOAD = re.compile(r"ldflda? .*(?:PlayerDrawSet::heldItem|Player::lastVisualizedSelectedItem)")
INSTANCE_DATA = re.compile(r"call(?:virt)? .*(?:GetGlobalItem|TryGetGlobalItem|get_ModItem|ModItem)\b")


def enabled_mods():
    with open(os.path.join(dump_il.MODS, "enabled.json"), encoding="utf-8") as handle:
        return json.load(handle)


def names_member(dll):
    """Whether the assembly's metadata contains either member name at all."""
    with open(dll, "rb") as handle:
        blob = handle.read()
    return any(member in blob for member in SNAPSHOT_MEMBERS)


def methods(lines):
    """Yield (name, body lines) for every method in an IL dump."""
    name, body = None, []
    for line in lines:
        if re.match(r"^\s*\.method\b", line):
            if name:
                yield name, body
            name, body = "?", []
        elif name == "?":
            found = re.search(r"\s(\S+) \(", line)
            if found:
                name = found.group(1)
        elif name:
            body.append(line)
            if re.match(r"^\s*\} // end of method (.*)", line):
                yield re.match(r"^\s*\} // end of method (.*)", line).group(1), body
                name, body = None, []
    if name:
        yield name, body


def main():
    for mod in enabled_mods():
        try:
            _, dll = dump_il.extracted(mod)
        except SystemExit as missing:
            print(f"{mod}: {missing}", file=sys.stderr)
            continue
        if not names_member(dll):
            continue
        with open(dump_il.il_dump(mod), encoding="utf-8", errors="replace") as dump:
            lines = dump.read().splitlines()
        for name, body in methods(lines):
            if not any(SNAPSHOT_LOAD.search(line) for line in body):
                continue
            reaches = sorted({found.group(0).split("::")[-1].split("<")[0]
                              for line in body if (found := INSTANCE_DATA.search(line))})
            print(f"{mod}: {name}" + (f"  -> {', '.join(reaches)}" if reaches else ""))


if __name__ == "__main__":
    main()
