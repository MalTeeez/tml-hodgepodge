"""Render a whole profile as an interactive flamegraph, with the idle time taken out.

attribute.py answers questions you already know to ask, and blames time on the frame you named.
This draws everything at once, which is what you want when the frame budget is full and the
per-frame costs found so far do not add up to it.

Two things it does that a generic flamegraph viewer does not:

  * Idle is dropped. A sampled thread-time trace is mostly threads parked in a wait, and a
    flamegraph of a modded Terraria client is otherwise 80% Wait/Sleep/Poll by area. An interval
    is dropped when the innermost real frame it stopped in is a wait, so a wait *inside* real
    work still disappears while the work around it stays. --include-waiting keeps them.
  * Frames are coloured and labelled by the mod that owns them, so a hot stripe can be read back
    to a mod without expanding it.

CPU_TIME and UNMANAGED_CODE_TIME are where the sampler stopped, not code, so they never appear as
frames -- their time lands on the deepest real frame beneath them.

  flamegraph.py <file.speedscope.json>              busiest thread, idle dropped
  flamegraph.py <file> --all-threads                every thread pooled
  flamegraph.py <file> --ticks 37776                label frames in ms/tick as well as ms
  flamegraph.py <file> --min-percent 0.02           keep thinner slivers (bigger output)
"""
import argparse
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from attribute import shorten  # noqa: E402  the same name-tidying attribute.py prints with

# Everything that ships with the game or the runtime. A first namespace segment outside this set
# is a mod, which is the whole of the ownership classification.
VANILLA = {
    "Terraria", "System", "Microsoft", "ReLogic", "MonoMod", "Mono", "Steamworks", "Newtonsoft",
    "Internal", "Interop", "SDL2", "NVorbis", "Ionic", "Hjson", "log4net", "RailSDK", "Delegate",
    "CPU_TIME", "UNMANAGED_CODE_TIME", "Process64", "dynamicClass", "MS", "FNA", "XNA", "Thread",
}

# Frames that mean "this thread is doing nothing". Matched as substrings against the innermost
# real frame; Game.Tick is FNA's frame limiter, which idles once the frame is already finished.
WAITING = (
    "ManualResetEventSlim.Wait", "LowLevelLifoSemaphore", "WaitHandle.Wait", "Monitor.Wait",
    "IOCompletionPoller.Poll", "Thread.Sleep", "SpinWait", "WaitForSingleObject",
    "Game.Tick()", "SemaphoreSlim.Wait", "Task.Wait", "ConditionVariable",
)

SYNTHETIC = ("CPU_TIME", "UNMANAGED_CODE_TIME", "Process64", "Thread (")

# `System.Void SOTS.SOTSDetours::Player_Update(...)` -- a return type, then the owner, then the
# signature. The owner is the last token before the arguments.
OWNER = re.compile(r"(?:^|\s)([A-Za-z_<>][\w.<>+`]*)(?:::|:|\.)[\w`<>$]+\s*\(")


def mod_of(name):
    """The mod owning a frame, or None for vanilla, the runtime and unparseable names."""
    name = name.split("!")[-1]
    if name.startswith(">?"):
        name = name.split("::", 1)[-1]
    match = OWNER.search(name)
    owner = match.group(1) if match else name.split("(")[0].strip()
    root = owner.split(".")[0].lstrip("<>") if owner else ""
    return None if not root or root in VANILLA else root


def parse_args():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("profile", help="speedscope json from `dotnet-trace convert`")
    parser.add_argument("-o", "--output", default=None, help="default: <profile>.flame.html")
    parser.add_argument("--thread", default=None, help="thread name substring; default busiest")
    parser.add_argument("--all-threads", action="store_true", help="pool every thread")
    parser.add_argument("--include-waiting", action="store_true", help="keep parked intervals")
    parser.add_argument("--ticks", type=int, default=0, help="frame or tick count, to label ms/tick")
    parser.add_argument("--min-percent", type=float, default=0.05,
                        help="drop frames narrower than this share of the thread (default 0.05)")
    return parser.parse_args()


def accumulate(thread, keep, is_synthetic, tree, counters):
    """Walk one thread's open/close events, adding each interval to the stack path it belongs to."""
    stack, previous = [], None
    for event in thread["events"]:
        now = event["at"]
        if stack and previous is not None:
            elapsed = now - previous
            counters["total"] += elapsed

            # The innermost real frame is where the sampler stopped, and so decides whether this
            # interval was work or a wait.
            innermost = next((index for index in reversed(stack) if not is_synthetic[index]), None)
            if innermost is None or not keep[innermost]:
                counters["idle"] += elapsed
            else:
                node = tree
                for index in stack:
                    if is_synthetic[index]:
                        continue
                    node = node[1].setdefault(index, [0.0, {}])
                    node[0] += elapsed

        if event["type"] == "O":
            stack.append(event["frame"])
        elif stack:
            stack.pop()
        previous = now


def to_json(node, frames, floor):
    """Compact the tree to [label, mod, value, children], pruning anything below the floor."""
    out = []
    for index, (value, children) in sorted(node.items(), key=lambda kv: frames[kv[0]]):
        if value < floor:
            continue
        out.append([shorten(frames[index]), mod_of(frames[index]) or "", round(value, 2),
                    to_json(children, frames, floor)])
    return out


def main():
    args = parse_args()
    document = json.load(open(args.profile))
    frames = [frame["name"] for frame in document["shared"]["frames"]]
    busiest = max(document["profiles"], key=lambda p: len(p.get("events", [])))

    if args.all_threads:
        threads = [p for p in document["profiles"] if p.get("events")]
    elif args.thread:
        threads = [p for p in document["profiles"] if args.thread in str(p.get("name"))]
    else:
        threads = [busiest]
    if not threads:
        sys.exit(f"no thread matching {args.thread!r}")

    is_synthetic = [any(mark in name for mark in SYNTHETIC) for name in frames]
    keep = [not any(mark in name for mark in WAITING) or args.include_waiting for name in frames]

    span = max(p["endValue"] - p["startValue"] for p in threads)
    tree, counters = [0.0, {}], {"total": 0.0, "idle": 0.0}
    for thread in threads:
        accumulate(thread, keep, is_synthetic, tree, counters)

    busy = counters["total"] - counters["idle"]
    label = (str(threads[0].get("name")) if len(threads) == 1
             else f"{len(threads)} threads pooled")
    root = to_json(tree[1], frames, span * args.min_percent / 100)

    # `<` inside a frame name would otherwise close the script element early.
    payload = json.dumps({
        "root": root, "span": span, "busy": busy, "idle": counters["idle"],
        "thread": label, "ticks": args.ticks, "source": os.path.basename(args.profile),
    }).replace("<", "\\u003c")

    output = args.output or args.profile.replace(".speedscope.json", "") + ".flame.html"
    open(output, "w", encoding="utf-8").write(TEMPLATE.replace("__DATA__", payload))
    print(f"{output}\n"
          f"  {label}   span {span / 1000:.2f}s\n"
          f"  busy {busy / 1000:.2f}s ({busy / span * 100:.1f}% of wall clock)\n"
          f"  idle {counters['idle'] / 1000:.2f}s dropped "
          f"({counters['idle'] / counters['total'] * 100:.1f}% of samples)")


TEMPLATE = r"""<!doctype html><html><head><meta charset="utf-8"><title>flamegraph</title>
<style>
 body{margin:0;font:12px ui-monospace,Consolas,monospace;background:#14161a;color:#d6dae1}
 header{padding:10px 14px;border-bottom:1px solid #2a2e36}
 h1{font-size:13px;margin:0 0 4px}
 #meta{color:#8b929e}
 #legend{padding:6px 14px;border-bottom:1px solid #2a2e36;line-height:1.9}
 #legend span{margin-right:10px;white-space:nowrap}
 #legend i{display:inline-block;width:9px;height:9px;margin-right:4px;border-radius:2px}
 #chart{position:relative}
 .f{position:absolute;height:15px;overflow:hidden;border-radius:2px;cursor:pointer;
    box-sizing:border-box;border:1px solid rgba(0,0,0,.35);padding-left:3px;line-height:14px;
    white-space:nowrap;color:#0d0f12;font-size:10px}
 .f:hover{filter:brightness(1.25)}
 #tip{position:fixed;pointer-events:none;background:#0b0d10;border:1px solid #3a4048;padding:6px 8px;
      border-radius:3px;max-width:820px;white-space:normal;display:none;z-index:9;font-size:11px}
 button{background:#232830;color:#d6dae1;border:1px solid #3a4048;border-radius:3px;padding:3px 9px;
        cursor:pointer;font:inherit}
</style></head><body>
<header><h1>Flamegraph <span id="src"></span></h1>
<div id="meta"></div><div style="margin-top:6px"><button id="reset">reset zoom</button>
<span id="crumb" style="color:#8b929e;margin-left:8px"></span></div></header>
<div id="legend"></div><div id="chart"></div><div id="tip"></div>
<script>
const D = __DATA__;
const chart=document.getElementById('chart'), tip=document.getElementById('tip');
const ROW=16, hue={};
// Stable colour per mod: hash the name to a hue so a mod keeps its colour between renders.
function colour(mod){
  if(!mod) return '#6b7480';
  if(hue[mod]===undefined){let h=0;for(const c of mod)h=(h*31+c.charCodeAt(0))>>>0;hue[mod]=h%360;}
  return `hsl(${hue[mod]} 62% 62%)`;
}
function ms(v){return v>=1000?(v/1000).toFixed(2)+'s':v.toFixed(1)+'ms';}
function self(n){return n[2]-n[3].reduce((a,c)=>a+c[2],0);}
function label(n){
  const pct=(n[2]/D.span*100).toFixed(2)+'%';
  const per=D.ticks?'  '+(n[2]/D.ticks).toFixed(3)+'ms/tick':'';
  const s=self(n);
  // Total and self are spelled out because a wide bar is nearly always its children, and
  // reading the width as self time is the easiest mistake to make with a flamegraph.
  return `${n[0]}\n${n[1]?'mod: '+n[1]:'vanilla / runtime'}\n`+
         `total ${ms(n[2])}  ${pct}${per}\n`+
         `self  ${ms(s)}  (${(s/n[2]*100).toFixed(1)}% of this bar, ${n[3].length} children)`;
}
let stack=[];                                   // zoom breadcrumb, root first
function draw(nodes, total){
  chart.innerHTML=''; let maxDepth=0;
  (function walk(list, depth, x0, width, span){
    maxDepth=Math.max(maxDepth,depth);
    // Children too thin to draw are pooled into one bar at the end instead of being skipped over.
    // A gap in the row above a frame is indistinguishable from that frame's self time, and these
    // are not self time -- a wide caller with many small callees would otherwise read as hot.
    const shown=[], thin=[];
    for(const n of list) (width*(n[2]/span)>0.35?shown:thin).push(n);

    let x=x0;
    const place=(n,w,fill,text,tooltip)=>{
      const d=document.createElement('div');
      // Anchored to the bottom, so the root sits on the floor and callees stack upward. Using
      // bottom rather than top means the depth of the tree does not have to be known first.
      d.className='f'; d.style.cssText=
        `left:${x}%;width:${w}%;bottom:${depth*ROW}px;background:${fill}`;
      if(w>2.2) d.textContent=text;
      d.onmousemove=e=>{tip.style.display='block';tip.textContent=tooltip;
        tip.style.left=Math.min(e.clientX+14,innerWidth-840)+'px';tip.style.top=(e.clientY+16)+'px';};
      d.onmouseleave=()=>tip.style.display='none';
      if(n) d.onclick=()=>{stack.push(n);render();};
      chart.appendChild(d);
    };

    for(const n of shown){
      const w=width*(n[2]/span);
      place(n,w,colour(n[1]),n[0],label(n));
      walk(n[3], depth+1, x, w, n[2]);
      x+=w;
    }
    if(thin.length){
      const value=thin.reduce((a,n)=>a+n[2],0), w=width*(value/span);
      const top=thin.slice().sort((a,b)=>b[2]-a[2]).slice(0,8)
        .map(n=>`  ${ms(n[2])}  ${n[0]}`).join('\n');
      place(null,w,'#464c56',`${thin.length} frames too thin to draw`,
        `${thin.length} frames too thin to draw, pooled\n`+
        `total ${ms(value)}  ${(value/D.span*100).toFixed(2)}%\n\nlargest:\n${top}`);
    }
  })(nodes,0,0,100,total);
  chart.style.height=((maxDepth+2)*ROW)+'px';
}
function render(){
  const node=stack[stack.length-1];
  draw(node?[node]:D.root, node?node[2]:D.busy);
  document.getElementById('crumb').textContent=stack.map(n=>n[0]).join('  >  ');
}
document.getElementById('reset').onclick=()=>{stack=[];render();};
document.getElementById('src').textContent=D.source;
document.getElementById('meta').innerHTML=
  `${D.thread} &nbsp;|&nbsp; span ${(D.span/1000).toFixed(2)}s &nbsp;|&nbsp; `+
  `busy ${(D.busy/1000).toFixed(2)}s &nbsp;|&nbsp; `+
  `idle ${(D.idle/1000).toFixed(2)}s dropped`+
  (D.ticks?` &nbsp;|&nbsp; ${D.ticks} ticks`:'');
// Legend covers the mods with visible area, biggest first.
const area={};
(function tally(list){for(const n of list){if(n[1])area[n[1]]=(area[n[1]]||0)+n[2];tally(n[3]);}})(D.root);
document.getElementById('legend').innerHTML=
  Object.entries(area).sort((a,b)=>b[1]-a[1]).slice(0,24)
    .map(([m,v])=>`<span><i style="background:${colour(m)}"></i>${m} ${(v/D.span*100).toFixed(1)}%</span>`)
    .join('')||'<span style="color:#8b929e">no mod frames above the threshold</span>';
render();
</script></body></html>"""


if __name__ == "__main__":
    main()
