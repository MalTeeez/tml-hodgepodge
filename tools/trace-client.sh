#!/bin/sh
# Capture and attribute a CPU profile of the running tModLoader client.
#
#   tools/trace-client.sh [seconds]            capture (default 120), convert, attribute
#   tools/trace-client.sh --analyze <trace>    re-attribute an existing capture
#
# Start a capture while already IN the scene being measured. Traces land in traces/,
# which is gitignored — they run to hundreds of megabytes.
#
# Three things this encodes, each of which has cost a run:
#   * `--profile cpu-sampling` is REJECTED by this dotnet-trace version. Omitting
#     --profile defaults to 'dotnet-common' + 'dotnet-sampled-thread-time', which is
#     the CPU sampling we want.
#   * --duration is dd:hh:mm:ss. 120 seconds is 00:00:02:00, not 00:00:120.
#   * --output must be a Windows path. A Git Bash /tmp/... path resolves somewhere the
#     tool cannot write and the file silently never appears. (Input paths are fine.)
set -e

trace_tool=~/.dotnet/tools/dotnet-trace
repo=$(cd "$(dirname "$0")/.." && pwd)
out_dir="$repo/traces"

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
    analyze "$2"
    exit 0
fi

seconds=${1:-120}
duration=$(printf "00:00:%02d:%02d" $((seconds / 60)) $((seconds % 60)))
mkdir -p "$out_dir"
trace="$out_dir/client-$(date +%Y%m%d-%H%M%S).nettrace"

# tModLoader runs a second process, `tModLoader.dll -terrariasteamclient <n>`, which also
# matches on name and is idle. Tracing it yields an empty capture that looks like a
# successful run. Exclude it, and any local dedicated server, then refuse to guess.
candidates=$("$trace_tool" ps \
    | grep -i 'tModLoader\.dll' \
    | grep -v -e terrariasteamclient -e ' -server')
count=$(printf '%s\n' "$candidates" | grep -c . || true)

if [ "$count" -eq 0 ]; then
    echo "tModLoader is not running. Launch it, get into the scene, then re-run."
    "$trace_tool" ps | grep -i tmodloader || echo "  (no tModLoader processes at all)"
    exit 1
elif [ "$count" -gt 1 ]; then
    echo "more than one candidate — pass the pid explicitly as the second argument:"
    printf '%s\n' "$candidates"
    exit 1
fi
pid=$(printf '%s\n' "$candidates" | awk '{print $1}')

echo "pid $pid  |  $duration  |  $trace"
echo "capturing — stay in the scene until this returns."
"$trace_tool" collect -p "$pid" --buffersize 512 --duration "$duration" \
    --output "$(cygpath -w "$trace" 2>/dev/null || echo "$trace")"

analyze "$trace"
