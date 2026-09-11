#!/bin/sh
# Capture and attribute a CPU profile of the running tModLoader client.
#
#   tools/trace-client.sh [seconds]               capture (default 120), convert, attribute
#   tools/trace-client.sh --until-stop [seconds]  capture segments (default 180) until --stop
#   tools/trace-client.sh --stop                  end the running segmented capture
#   tools/trace-client.sh --analyze <trace|dir>   re-attribute an existing capture
#
# Start a capture while already IN the scene being measured. Traces land in traces/,
# which is gitignored — they run to hundreds of megabytes.
#
# Four things this encodes, each of which has cost a run:
#   * `--profile cpu-sampling` is REJECTED by this dotnet-trace version. Omitting
#     --profile defaults to 'dotnet-common' + 'dotnet-sampled-thread-time', which is
#     the CPU sampling we want.
#   * --duration is dd:hh:mm:ss. 120 seconds is 00:00:02:00, not 00:00:120.
#   * --output must be a Windows path. A Git Bash /tmp/... path resolves somewhere the
#     tool cannot write and the file silently never appears. (Input paths are fine.)
#   * dotnet-trace finalizes a trace only when --duration expires. It ignores WM_CLOSE
#     and a genuine console CTRL_C_EVENT, so an open ended `collect` cannot be ended
#     cleanly, and killing it loses the end of session rundown -- the record that names
#     every method JIT'd before the capture began, which for an already running game is
#     nearly all of them. Such a trace converts without error into nothing but CPU_TIME
#     and UNMANAGED_CODE_TIME. That is why --until-stop chains fixed durations.
set -e

trace_tool=~/.dotnet/tools/dotnet-trace
repo=$(cd "$(dirname "$0")/.." && pwd)
out_dir="$repo/traces"

# dotnet-trace writes through the Windows API, so it needs a Windows output path.
winpath() {
    cygpath -w "$1" 2>/dev/null || echo "$1"
}

# tModLoader runs a second process, `tModLoader.dll -terrariasteamclient <n>`, which also
# matches on name and is idle. Tracing it yields an empty capture that looks like a
# successful run. Exclude it, and any local dedicated server, then refuse to guess.
resolve_pid() {
    candidates=$("$trace_tool" ps \
        | grep -i 'tModLoader\.dll' \
        | grep -v -e terrariasteamclient -e ' -server')
    count=$(printf '%s\n' "$candidates" | grep -c . || true)

    if [ "$count" -eq 0 ]; then
        echo "tModLoader is not running. Launch it, get into the scene, then re-run." >&2
        "$trace_tool" ps | grep -i tmodloader >&2 || echo "  (no tModLoader processes at all)" >&2
        exit 1
    elif [ "$count" -gt 1 ]; then
        echo "more than one candidate — pass the pid explicitly as the second argument:" >&2
        printf '%s\n' "$candidates" >&2
        exit 1
    fi
    printf '%s\n' "$candidates" | awk '{print $1}'
}

as_duration() {
    printf "00:00:%02d:%02d" $(($1 / 60)) $(($1 % 60))
}

collect() {
    "$trace_tool" collect -p "$1" --buffersize 512 --duration "$2" --output "$(winpath "$3")"
}

analyze() {
    trace="$1"
    [ -s "$trace" ] || { echo "no trace at $trace"; exit 1; }
    speedscope="${trace%.nettrace}.speedscope.json"

    if [ ! -s "$speedscope" ]; then
        echo "converting $(du -h "$trace" | cut -f1)..."
        "$trace_tool" convert "$trace" --format speedscope
    fi
    [ -s "$speedscope" ] || { echo "conversion produced nothing"; exit 1; }

    echo
    python "$repo/tools/attribute.py" "$speedscope" --top 14
    echo
    echo "drill further with:"
    echo "  python tools/attribute.py $speedscope --roots <frame substring>"
    echo "  python tools/attribute.py $speedscope --callers <frame substring>"
}

if [ "$1" = "--analyze" ]; then
    if [ -d "$2" ]; then
        for segment in "$2"/*.nettrace; do
            echo "=== $segment ==="
            analyze "$segment"
        done
    else
        analyze "$2"
    fi
    exit 0
fi

# Deleting the sentinel is what ends a --until-stop run. Killing the script does not: the
# collect it started is a separate process that outlives it and keeps writing, and on
# Windows there is no process group to take down with the parent. The loop therefore asks
# a file whether to keep going, which no signal has to be delivered for.
sentinel="CAPTURING"

if [ "$1" = "--stop" ]; then
    newest=$(ls -d "$out_dir"/session-*/ 2>/dev/null | tail -1)
    [ -n "$newest" ] || { echo "no capture session to stop"; exit 1; }
    rm -f "$newest$sentinel"
    echo "stopping after the segment in flight — it still lands in $newest"
    exit 0
fi

# Back to back fixed length captures, for a scene whose end is not known in advance -- a
# boss fight that runs until it runs. Each segment ends with a full method name rundown,
# which for this mod pack is around 200 MB and stalls the game while it is written, so a
# segment boundary is a visible hitch and short segments are unusable. Nothing is analysed
# here either, because converting one segment while the next records steals the CPU being
# measured; run --analyze over the session directory once the scene is over.
if [ "$1" = "--until-stop" ]; then
    seconds=${2:-180}
    if [ "$seconds" -lt 60 ]; then
        echo "segments shorter than 60s stall the game more than they measure it." >&2
        exit 1
    fi

    duration=$(as_duration "$seconds")
    session="$out_dir/session-$(date +%Y%m%d-%H%M%S)"
    mkdir -p "$session"
    touch "$session/$sentinel"
    pid=$(resolve_pid)

    echo "pid $pid  |  ${seconds}s segments  |  $session"
    echo "stop with: tools/trace-client.sh --stop"
    segment=1
    while [ -f "$session/$sentinel" ]; do
        trace=$(printf "%s/seg-%02d.nettrace" "$session" "$segment")
        collect "$pid" "$duration" "$trace" >/dev/null
        echo "  -> $trace"
        segment=$((segment + 1))
    done
    echo "stopped after $((segment - 1)) segments"
    exit 0
fi

seconds=${1:-120}
duration=$(as_duration "$seconds")
mkdir -p "$out_dir"
trace="$out_dir/client-$(date +%Y%m%d-%H%M%S).nettrace"
pid=$(resolve_pid)

echo "pid $pid  |  $duration  |  $trace"
echo "capturing — stay in the scene until this returns."
collect "$pid" "$duration" "$trace"

analyze "$trace"
