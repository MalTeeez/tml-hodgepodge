"""Rank every frame by own work, with CPU_TIME/UNMANAGED folded onto the deepest real frame.

attribute.py's self-time table is dominated by the two synthetic frames, which say where the
sampler stopped rather than who is at fault. Dropping them onto the real frame beneath is the
attribution that names code, and ranking everything at once is what finds the costs nobody
thought to query.
"""
import collections
import json
import sys

SYNTHETIC = ("CPU_TIME", "UNMANAGED_CODE_TIME")

path = sys.argv[1]
top = int(sys.argv[2]) if len(sys.argv) > 2 else 45
document = json.load(open(path))
frames = [frame["name"] for frame in document["shared"]["frames"]]
profile = max(document["profiles"], key=lambda p: len(p.get("events", [])))
span = profile["endValue"] - profile["startValue"]

own = collections.Counter()
stack, previous = [], None
for event in profile["events"]:
    now = event["at"]
    if stack and previous is not None:
        # Charge the interval to the innermost frame that is actual code.
        for index in reversed(stack):
            if not frames[index].startswith(SYNTHETIC):
                own[index] += now - previous
                break
    if event["type"] == "O":
        stack.append(event["frame"])
    elif stack:
        stack.pop()
    previous = now

print(f"=== {path.split('/')[-1]}   span {span / 1000:.2f}s   own work, synthetics folded in ===")
for index, elapsed in own.most_common(top):
    print(f"  {elapsed / span * 100:6.2f}%  {elapsed:9.1f}ms  {frames[index][:96]}")
