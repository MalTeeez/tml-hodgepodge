"""Attribute a dotnet-trace speedscope conversion to the code that actually costs time.

The speedscope files dotnet-trace emits are 'evented' (open/close pairs), not 'sampled',
so a call tree has to be reconstructed by walking the events with a stack.

  attribute.py <file.json>                          top self time, and auto-detected roots
  attribute.py <file.json> --roots DoDraw DoUpdate  direct children of matching frames
  attribute.py <file.json> --callers DateTime.get_Now   reverse lookup: who calls this
  attribute.py <file.json> --ticks 1789             also report ms/tick, not just %

Read --roots output top-down and descend into whatever dominates. Two traps, both of
which have produced wrong conclusions in this project already:

  1. A detour carries the inclusive time of everything nested inside it. A mod can read
     60% of the thread and cost nothing. Check its own CPU_TIME before blaming it.
  2. Self-time under a detour is per-call overhead times call frequency, and frequencies
     differ by orders of magnitude. Normalise before ranking detours against each other.
"""
import argparse
import collections
import json


def parse_args():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("profile", help="speedscope json from `dotnet-trace convert`")
    parser.add_argument("--roots", nargs="*", default=None,
                        help="frame substrings to expand; omit for auto-detection")
    parser.add_argument("--callers", default=None, help="reverse lookup for a frame substring")
    parser.add_argument("--thread", default=None, help="thread name substring; default busiest")
    parser.add_argument("--ticks", type=int, default=0, help="tick count, to report ms/tick")
    parser.add_argument("--top", type=int, default=18, help="rows per section")
    return parser.parse_args()


def shorten(name):
    """Strip assembly prefixes and MonoMod's generated wrapper noise."""
    name = name.split("!")[-1]
    for prefix in ("dynamicClass.", "DMD<", "SyncProxy<", "Hook<"):
        name = name.replace(prefix, "")
    return name[:88]


def pick_thread(profiles, hint):
    if hint:
        match = next((p for p in profiles if hint in p.get("name", "")), None)
        if match:
            return match
    return max(profiles, key=lambda p: len(p.get("events", [])))


def walk(profile, frames):
    """Yield (elapsed_ms, [frame names]) for every interval between events."""
    stack, previous = [], None
    for event in profile["events"]:
        now = event["at"]
        if stack and previous is not None:
            yield now - previous, [frames[index] for index in stack]
        if event["type"] == "O":
            stack.append(event["frame"])
        elif stack:
            stack.pop()
        previous = now


def main():
    args = parse_args()
    document = json.load(open(args.profile))
    frames = [frame["name"] for frame in document["shared"]["frames"]]
    profile = pick_thread(document["profiles"], args.thread)
    span = profile["endValue"] - profile["startValue"]

    # Auto-detect entry points when none were named. Covers client and server shapes.
    roots = args.roots
    if roots is None:
        roots = ["Main:DoDraw", "Main::DoDraw", "Main:DoUpdate", "Main::DoUpdate",
                 "DoUpdateInWorld"]

    self_time = collections.Counter()
    children = collections.defaultdict(collections.Counter)
    callers = collections.Counter()
    caller_total = 0.0

    for elapsed, names in walk(profile, frames):
        self_time[names[-1]] += elapsed
        for depth, name in enumerate(names[:-1]):
            if any(root in name for root in roots):
                children[name][names[depth + 1]] += elapsed
        if args.callers:
            for depth, name in enumerate(names):
                if args.callers in name:
                    caller_total += elapsed
                    # nearest real ancestor, skipping synthetic frames
                    for above in range(depth - 1, -1, -1):
                        if "CPU_TIME" not in names[above] and "UNMANAGED" not in names[above]:
                            callers[names[above]] += elapsed
                            break
                    break

    def rate(value):
        share = f"{value / span * 100:6.2f}%"
        return f"{share} {value / args.ticks:8.3f}ms/tick" if args.ticks else f"{share} {value:9.1f}ms"

    print(f"thread {profile.get('name')}   span {span / 1000:.2f}s   "
          f"events {len(profile['events'])}\n")

    if args.callers:
        print(f"=== {args.callers}: {rate(caller_total)} total, called from ===")
        for name, value in callers.most_common(args.top):
            print(f"  {rate(value)}  {shorten(name)}")
        return

    print("=== top self time (synthetic CPU_TIME/UNMANAGED frames are not a mod) ===")
    for name, value in self_time.most_common(args.top):
        print(f"  {rate(value)}  {shorten(name)}")

    for root in sorted(children, key=lambda r: -sum(children[r].values())):
        total = sum(children[root].values())
        if total / span < 0.005:
            continue
        print(f"\n=== inside {shorten(root)}  [{rate(total)}] ===")
        for name, value in children[root].most_common(args.top):
            print(f"  {rate(value)}  {shorten(name)}")


if __name__ == "__main__":
    main()
