"""Find code that would break if an empty SpriteBatch flush stopped applying render state.

The proposed FNA patch moves `if (numSprites == 0) return;` above `PrepRenderState()` in
FlushBatch. Batches that drew at least one sprite are unaffected -- they still flush, and still
apply state. What changes is the empty batch: its End() stops touching the device, so anything
that was inheriting BlendState / SamplerStates / DepthStencilState / RasterizerState from that
flush now sees whatever the previous batch left.

Only raw GraphicsDevice draws inherit that state; sprites drawn through SpriteBatch bring their
own. So the risk set is methods that issue raw primitive draws near a SpriteBatch End without
setting the state they need themselves.

  scan_flushstate.py <dir-of-extracted-mods>

A static scan cannot decide emptiness -- whether a batch draws zero sprites is a runtime
property of the branches inside it. This narrows a 74 mod pack to a reviewable list; it does
not prove the patch safe.

It did not. The patch this was written for came back clean here, shipped off by default, and
drew walls at the wrong screen offset the first time it was switched on with FancyLighting
installed. It is gone; this stays as the worked example for the next patch to shared
infrastructure, and as the measure of what a clean result is worth.
"""
import pathlib
import re
import subprocess
import sys

METHOD_START = re.compile(r"^\s*\.method\s")
METHOD_NAME = re.compile(r"^\s*(?:instance\s+)?[\w`<>$.\[\]/]+\s+([\w`<>$.]+)\s*\(")

# Raw draws read their render state off the device, so they are the only ones that can inherit
# it from someone else's flush.
RAW_DRAW = re.compile(r"GraphicsDevice::Draw(?:User)?(?:Indexed)?Primitives[<(]")
# A method that sets the state it needs is self-sufficient no matter what the last flush did.
SETS_STATE = re.compile(r"GraphicsDevice::set_(?:BlendState|DepthStencilState|RasterizerState)"
                        r"|GraphicsDevice::get_SamplerStates|SamplerStateCollection::set_Item")
SB_BEGIN = re.compile(r"SpriteBatch::Begin\(")
SB_END = re.compile(r"SpriteBatch::End\(\)")
SB_DRAW = re.compile(r"SpriteBatch::Draw(?:String)?\(")


def methods(il):
    """Yield (name, [lines]) for every method body in an ilspycmd -il dump."""
    name, in_header, body = "?", False, []
    for line in il:
        if METHOD_START.match(line):
            if body:
                yield name, body
            name, in_header, body = "?", True, []
            continue
        if in_header:
            found = METHOD_NAME.match(line)
            if found:
                name, in_header = found.group(1), False
            continue
        body.append(line)
    if body:
        yield name, body


def scan(dll):
    try:
        il = subprocess.run(["ilspycmd", "-il", str(dll)], capture_output=True, text=True,
                            timeout=600).stdout.splitlines()
    except (subprocess.TimeoutExpired, OSError) as error:
        print(f"  ! {dll.name}: {error}")
        return

    for name, body in methods(il):
        text = "\n".join(body)
        if not RAW_DRAW.search(text):
            continue
        if SETS_STATE.search(text):
            continue                                  # sets its own state, cannot inherit wrongly
        if not (SB_END.search(text) or SB_BEGIN.search(text)):
            continue                                  # no batch boundary anywhere near it

        # A batch that provably queues a sprite is never the empty one this patch changes.
        drew = bool(SB_DRAW.search(text))
        yield name, ("begin+end, no SpriteBatch.Draw" if not drew else "draws sprites too")


def main():
    root = pathlib.Path(sys.argv[1])
    flagged = 0
    for dll in sorted(root.glob("*/*.dll")):
        if dll.stem != dll.parent.name:
            continue
        hits = list(scan(dll))
        if hits:
            flagged += len(hits)
            print(f"\n{dll.stem}")
            for name, note in hits:
                print(f"    {name:<52} {note}")
    print(f"\n{flagged} methods to review")


if __name__ == "__main__":
    main()
