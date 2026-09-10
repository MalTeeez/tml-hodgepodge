"""Convert a dotnet-trace profile into `perf script` text for profiler.firefox.com.

The Firefox Profiler is worth the trip for the things flamegraph.py deliberately does not do:
a timeline you can select a single frame out of, an inverted call tree, and per-thread tracks.

Of the formats it imports, `perf script` is the one that keeps both timestamps and threads, and
it carries symbol names inline -- so nothing has to serve symbols and there is no schema version
to get wrong. Load the output at https://profiler.firefox.com by dropping the file on the page.

The .NET assembly becomes the perf "module", so the profiler's library grouping splits vanilla,
FNA, the runtime and each mod apart on its own. Frames with no assembly prefix are labelled with
the mod that owns them.

Stacks here run about thirty frames deep, because every mod detour expands into three MonoMod
trampoline frames, so a full trace is gigabytes of text that no browser will open. The window is
therefore shortened until it fits --max-mb, and every sample inside it is kept.

Thinning would be the obvious alternative and it is not safe: the profiler infers a sample's
duration from the gap to the next one, so dropping samples hands their time to whichever sample
came before. Measured on this pack, one in three turned Main.DoDraw's self time from 0.06% of
its bar into 18.78%. --every still exists for when size beats accuracy, and says so when used.

  firefox_profile.py <file.speedscope.json>                 all threads, as long a window as fits
  firefox_profile.py <file> --max-mb 400                    a longer window, slower to load
  firefox_profile.py <file> --start 120 --seconds 30        a specific window
  firefox_profile.py <file> --thread 24508                  one thread, so the window reaches wider
"""
import argparse
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from flamegraph import SYNTHETIC, WAITING, mod_of  # noqa: E402  one definition of "idle"

TID = re.compile(r"(\d+)")

# The stack emitted for a parked sample. The importer skips a function name that opens with a
# paren -- that is how it discards process-name lines -- so this cannot be spelled "(idle)".
IDLE = "\t0 idle (parked)"


def parse_args():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("profile", help="speedscope json from `dotnet-trace convert`")
    parser.add_argument("-o", "--output", default=None, help="default: <profile>.perf")
    parser.add_argument("--thread", default=None,
                        help="thread name substring; default every thread")
    parser.add_argument("--start", type=float, default=0.0, help="seconds into the trace")
    parser.add_argument("--seconds", type=float, default=None,
                        help="window length; default the whole trace")
    parser.add_argument("--max-mb", type=float, default=150.0,
                        help="size budget; the window shortens to fit (default 150, 0 for no cap)")
    parser.add_argument("--every", type=int, default=0,
                        help="keep one sample in N: smaller, but overstates self time at idle "
                             "boundaries. Prefer a shorter window")
    parser.add_argument("--period", type=float, default=1.0,
                        help="ms of wall clock per emitted sample (default 1.0)")
    parser.add_argument("--include-waiting", action="store_true", help="keep parked samples")
    return parser.parse_args()


PMT = re.compile(r"\(pMT: [^)]*\)")


def frame_line(name, address):
    """One perf stack line: `<addr> <function> (<module>)`, module required by the importer."""
    assembly, _, method = name.rpartition("!")
    if not assembly:
        method, assembly = name, mod_of(name) or "vanilla"

    # Method table pointers are runtime addresses, and they are most of the length of a mangled
    # name. Dropping them shrinks the file by roughly a third, which buys back resolution that
    # would otherwise have to be thinned away.
    method = PMT.sub("", method)

    # The importer finds the module as the last parenthesised group on the line, and its contents
    # cannot contain a closing paren. Method signatures may end in one, which is fine -- only the
    # module has to stay clean.
    return f"\t{address:x} {method.strip()} ({assembly.replace(')', '').strip()})"


def intervals(thread, keep_frame, is_synthetic, window):
    """Yield (start_ms, duration_ms, stack); stack is None wherever the thread was parked.

    Parked samples cannot simply be left out. The Firefox Profiler derives a sample's duration
    from the gap to the next one, so removing a run of them stretches the sample in front of the
    run across the whole gap -- which lands as self time on whatever was running just before the
    thread went idle. On a frame-limited client that is the end of the draw, so DoDraw and its
    children absorb the wait and appear to have self time they do not have.

    Emitting them as a one frame `idle` stack keeps every timestamp honest, and collapses into a
    single bar that is easy to ignore in the profiler.
    """
    start, end = window
    stack, previous = [], None
    for event in thread["events"]:
        now = event["at"]
        if stack and previous is not None and now > previous and start <= previous < end:
            # The innermost real frame is where the sampler stopped, so it decides whether this
            # interval was work or a wait -- the same rule flamegraph.py filters on.
            innermost = next((index for index in reversed(stack) if not is_synthetic[index]), None)
            if innermost is not None:
                yield previous, now - previous, (
                    [index for index in stack if not is_synthetic[index]]
                    if keep_frame[innermost] else None)

        if event["type"] == "O":
            stack.append(event["frame"])
        elif stack:
            stack.pop()
        previous = now


def resample(thread, keep_frame, is_synthetic, window, period):
    """Yield (time_ms, stack) once per `period` of wall clock, with the stack live at that moment.

    The conversion is evented: it records only changes, so a stack that survives ten sampling
    periods is a single long interval, while the instant between one child call returning and the
    next being made is an interval of a few microseconds. The Firefox Profiler counts samples
    rather than weighing them by their timestamps, so one perf sample per interval hands that
    instant the same weight as the ten periods. Measured on this pack, that made Main.DoDraw look
    30% self time when it is 0.1% -- its self intervals average 0.004ms against 1.087ms overall.

    Emitting on a fixed cadence instead makes sample count proportional to time, which is what
    the profiler is counting, and drops the sub-period instants that were never worth a sample.
    """
    cursor = None
    for at, duration, stack in intervals(thread, keep_frame, is_synthetic, window):
        cursor = at if cursor is None else max(cursor, at)
        while cursor < at + duration:
            yield cursor, stack
            cursor += period


def main():
    args = parse_args()
    document = json.load(open(args.profile))
    frames = [frame["name"] for frame in document["shared"]["frames"]]
    busiest = max(document["profiles"], key=lambda p: len(p.get("events", [])))

    threads = [p for p in document["profiles"]
               if p.get("events") and (not args.thread or args.thread in str(p.get("name")))]
    if not threads:
        sys.exit(f"no thread matching {args.thread!r}")

    is_synthetic = [any(mark in name for mark in SYNTHETIC) for name in frames]
    keep_frame = [not any(mark in name for mark in WAITING) or args.include_waiting
                  for name in frames]

    origin = min(p["startValue"] for p in threads)
    span = max(p["endValue"] for p in threads) - origin
    length = span / 1000 if args.seconds is None else args.seconds
    window = (origin + args.start * 1000, origin + (args.start + length) * 1000)

    # Every frame's line is the same however often it is sampled, so measuring them once turns the
    # counting pass into a near exact size. The address is the frame's depth, up to two hex digits.
    line_length = [len(frame_line(name, 0xFF)) + 1 for name in frames]
    header_length = 45

    def measure(candidates, over):
        """Bytes the window would produce, and the threads that did any work inside it.

        Resampling gives every thread a sample per period, so threads that never ran would
        otherwise fill the file with idle padding for tracks that show nothing.
        """
        kept, size = [], 0
        for thread in candidates:
            busy, bytes_used = 0, 0
            for _, stack in resample(thread, keep_frame, is_synthetic, over, args.period):
                if stack is None:
                    bytes_used += header_length + len(IDLE) + 1
                else:
                    busy += 1
                    bytes_used += header_length + sum(line_length[frame] for frame in stack)
            if busy:
                kept.append(thread)
                size += bytes_used
        return size, kept

    total, threads = measure(threads, window)
    if not threads:
        sys.exit("every thread was idle for the whole window")

    # Thinning looks like the obvious way to fit a budget, and it is wrong here. The profiler
    # infers a sample's duration from the gap to the next one, so a dropped sample hands its time
    # to the one before it. That is not the uniform subsample it appears to be: idle boundaries
    # come round once a frame, and the samples that survive next to them are the ones inside the
    # draw's own prologue, which then absorb the wait. Measured on a frame-limited client, one in
    # three turned Main.DoDraw's self time from 0.06% of its bar into 18.78%.
    #
    # Shortening the window instead keeps every surviving weight exact, and only the last sample
    # at the boundary is wrong. So the budget buys less time rather than worse time.
    budget = args.max_mb * 1e6
    every = args.every or 1
    # Samples are not spread evenly over a run -- a busier scene has deeper stacks and costs more
    # per second -- so scaling the window by the size ratio overshoots. Re-measure and correct.
    for _ in range(3):
        if args.every or not budget or total <= budget:
            break
        length *= budget / total
        window = (window[0], window[0] + length * 1000)
        total, threads = measure(threads, window)

    output = args.output or args.profile.replace(".speedscope.json", "") + ".perf"
    written = 0
    # newline="\n": the importer splits on \n, so Windows line endings would leave a stray \r on
    # every line for its regexes to trip over.
    with open(output, "w", encoding="utf-8", newline="\n") as out:
        # The importer sniffs the format from the very first line: either this exact header
        # sentinel, or a line that already parses as a sample. A friendly comment in front of it
        # is not recognised and the whole file is rejected.
        out.write("# ========\n"
                  f"# converted from {os.path.basename(args.profile)} by firefox_profile.py\n"
                  "# ========\n#\n")

        for thread in threads:
            found = TID.search(str(thread.get("name", "")))
            tid = int(found.group(1)) if found else 0
            name = "Main" if thread is busiest else f"Thread-{tid}"

            last_kind = None
            for index, (at, stack) in enumerate(
                    resample(thread, keep_frame, is_synthetic, window, args.period)):
                # Always keep the sample that starts a run, so an idle boundary never moves.
                starts_run = (stack is None) != last_kind  # stride only, when --every is given
                last_kind = stack is None
                if index % every and not starts_run:
                    continue

                out.write(f"{name} 0/{tid} {(at - origin) / 1000:.6f}: cpu-clock:\n")
                if stack is None:
                    out.write(IDLE + "\n")
                else:
                    # perf lists the leaf first; the importer reverses it back to root first.
                    out.writelines(frame_line(frames[frame], depth) + "\n"
                                   for depth, frame in reversed(list(enumerate(stack))))
                out.write("\n")
                written += 1

    thinned = ("" if every == 1 else
               f"\n  THINNED 1 in {every}: self time near idle boundaries will be overstated")
    print(f"{output}\n"
          f"  {len(threads)} thread(s), {written} samples, "
          f"{os.path.getsize(output) / 1e6:.1f} MB{thinned}\n"
          f"  window {args.start:.1f}s to {args.start + length:.1f}s of {span / 1000:.0f}s"
          f"{'' if length * 1000 >= span else f' (shortened to fit {args.max_mb:g} MB)'}\n"
          f"  load it by dropping the file onto https://profiler.firefox.com")


if __name__ == "__main__":
    main()
