"""Find code that would notice if ParticlePool stopped handing out the lowest free particle.

The proposed patch resumes RequestParticle's search where the last one succeeded and wraps,
instead of restarting at index zero. It still returns a particle that was resting and still
marks it fetched, and it still falls through to vanilla when nothing is resting, so the pool
grows exactly as before. One thing changes: which slot a request gets.

That is observable in two ways. A pool's list is drawn front to back by its renderer, so reuse
order is draw order within that pool -- a z-order change between particles of one type. And any
code that indexes a pool's backing list, or assumes a particular particle instance comes back,
is reading an order that is no longer the same.

  scan_particlepool.py <dir-of-extracted-mods>

A static scan cannot decide whether a z-order change is visible: that is a property of what the
particles look like and how much they overlap. This narrows the pack to what could notice; it
does not prove the patch safe, and it holds for the current mod list and no other.

Read the header of scan_flushstate.py before trusting a clean result here. That scan came back
clean, its patch shipped off by default, and it still broke wall rendering the first time it was
switched on. A clean result is permission to test, never permission to keep.
"""
import pathlib
import re
import subprocess
import sys

# Anything that owns a pool and could depend on the order it hands slots out.
POOL_USE = re.compile(r"ParticlePool`?1?(?:<[^>]*>)?::(?:RequestParticle|\.ctor)")
# Reaching the backing list at all means reading the order directly rather than through a request.
POOL_FIELD = re.compile(r"ParticlePool`?1?(?:<[^>]*>)?::_particles")
# A mod with its own pool type is unaffected; flag only the ones touching vanilla's.
VANILLA_POOL = re.compile(r"Terraria\.Graphics\.Renderers\.ParticlePool")
# Implementors decide what resting means, so a custom one is worth a look alongside its caller.
POOLED_PARTICLE = re.compile(r"Terraria\.Graphics\.Renderers\.IPooledParticle")

METHOD_START = re.compile(r"^\s*\.method\s")
METHOD_NAME = re.compile(r"^\s*(?:instance\s+)?[\w`<>$.\[\]/]+\s+([\w`<>$.]+)\s*\(")


def disassemble(assembly):
    """IL text for one assembly, or None when ilspycmd cannot read it."""
    try:
        return subprocess.run(["ilspycmd", "-il", str(assembly)], capture_output=True,
                              text=True, timeout=600).stdout
    except (subprocess.SubprocessError, OSError) as error:
        print(f"  !! {assembly.name}: {error}", file=sys.stderr)
        return None


def methods(il):
    """Yield (name, body text) for every method in a disassembly."""
    name, body = None, []
    for line in il.split("\n"):
        if METHOD_START.match(line):
            if name:
                yield name, "\n".join(body)
            name, body = "<unnamed>", []
        elif name is None:
            continue
        else:
            body.append(line)
            match = METHOD_NAME.match(line)
            if match and body.count(line) == 1 and name == "<unnamed>":
                name = match.group(1)
    if name:
        yield name, "\n".join(body)


def references_pool(assembly):
    """Cheap metadata pre-filter: a mod that never names the type cannot be affected by it.

    Disassembling a 74 mod pack costs half an hour; the type name appears verbatim in the
    metadata string heap of anything referencing it, so reading the bytes skips almost all of it.
    """
    try:
        return b"ParticlePool" in assembly.read_bytes()
    except OSError:
        return True


def scan(assembly):
    """Methods in one assembly that touch vanilla's particle pool."""
    if not references_pool(assembly):
        return []

    il = disassemble(assembly)
    if not il or not VANILLA_POOL.search(il):
        return []

    found = []
    for name, body in methods(il):
        if not VANILLA_POOL.search(body) and not POOLED_PARTICLE.search(body):
            continue

        if POOL_FIELD.search(body):
            found.append((name, "reads the pool's list directly"))
        elif POOL_USE.search(body):
            found.append((name, "requests from a pool"))
        elif POOLED_PARTICLE.search(body):
            found.append((name, "implements or consumes IPooledParticle"))

    return found


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 1

    root = pathlib.Path(sys.argv[1])
    assemblies = sorted(root.rglob("*.dll"))
    if not assemblies:
        print(f"no assemblies under {root}")
        return 1

    print(f"scanning {len(assemblies)} assemblies for particle pool order dependence\n")
    total = 0
    for assembly in assemblies:
        hits = scan(assembly)
        if not hits:
            continue

        print(f"{assembly.name}")
        for name, why in hits:
            print(f"    {name}  --  {why}")
        print()
        total += len(hits)

    print(f"{total} methods to review" if total
          else "nothing found -- permission to test, not permission to keep")
    return 0


if __name__ == "__main__":
    sys.exit(main())
