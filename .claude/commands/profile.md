---
description: Profile the Terraria client or server, attribute hotspots, propose patches, implement approved ones
argument-hint: [client|server] [seconds | until-stop, default 60]
allowed-tools: Bash, Read, Write, Edit, Glob, Grep
---

Run the full Hodgepodge loop against **$1** for **$2**.

`$2` is a number of seconds, or `until-stop` when the scene's end is not known in advance
— a boss fight that runs until it runs. Default 60 if unset. Anything else in the
arguments is the user describing the scene, not a duration: read it as context and do not
substitute it into a command. A word where a number belongs means they did not give one.

Work through the phases in order. **Stop at the end of phase 3 and wait for approval.**
Phase 4 runs only on explicit confirmation of specific patches.

---

## Phase 1 — Capture

Write the trace to the scratchpad, never into this repo.

### If target is `client`

The game must already be running and **in the scene that hurts** before you start. If it
is not running, say so and stop — do not capture an idle menu.

`tools/trace-client.sh` resolves the pid, captures, converts and attributes in one step.
Use it rather than driving `dotnet-trace` by hand.

```
tools/trace-client.sh 60                    a fixed window
tools/trace-client.sh --until-stop 180      segments, until the user says stop
tools/trace-client.sh --stop                end that, after the segment in flight
tools/trace-client.sh --analyze <dir>       attribute a finished session
```

Four traps, all of which have already cost a run. The script encodes them; they are here
because reaching for `dotnet-trace` directly reintroduces all four.

- **Do not pass `--profile cpu-sampling`.** This dotnet-trace version rejects it with
  *"does not apply to `dotnet-trace collect`"*. Omitting `--profile` gives CPU sampling
  plus the runtime provider, which is what is wanted.
- `--duration` is `dd:hh:mm:ss`. 60 seconds is `00:00:01:00`, not `00:00:60`.
- The `--output` path must be a **Windows** path. A Git Bash `/tmp/...` path silently
  resolves somewhere the tool cannot write and the file never appears.
- **There is no such thing as an open ended capture.** dotnet-trace finalizes a trace only
  when `--duration` expires. It ignores `WM_CLOSE` and a genuine console `CTRL_C_EVENT`,
  so a `collect` started without `--duration` can only be force killed, and that loses the
  end of session rundown — the record naming every method JIT'd before the capture began,
  which for an already running game is nearly all of them. The conversion then succeeds,
  warns *"Detected a potentially broken trace"*, and yields a profile that is 100%
  `CPU_TIME` and `UNMANAGED_CODE_TIME` with not one managed frame. Ten minutes of a boss
  fight have already been lost this way.

### Capturing until the user says stop

`--until-stop` records back to back `--duration` segments into `traces/session-<stamp>/`,
each one finalized and convertible on its own. Run it in the background, and end it with
`tools/trace-client.sh --stop` when the user says the scene is over.

**Every segment boundary freezes the game for a moment, and the user will feel it.** The
rundown written at the end of each segment is about 200 MB for this mod pack, and it is a
fixed cost that does not shrink with the segment — a five second segment produced a 202 MB
file, almost all of it rundown. That is the whole reason to prefer long segments. Three
minutes is the default and the floor is sixty seconds. Say this up front when starting an
open ended capture, so the hitching is expected rather than reported as a new bug.

Stop it with `--stop`, not by killing the task. The loop asks a sentinel file whether to
continue, because killing the script does **not** stop it: the `collect` it started is a
separate process that outlives it, and on Windows there is no process group to take down
with the parent. A killed `--until-stop` keeps writing segments until something removes
the sentinel or kills every `dotnet-trace` by pid. That has already happened once.

Either way the segment in flight still lands. It finishes its own `--duration` and writes
a complete file, so stopping costs nothing beyond waiting it out — check for the last
`seg-NN.nettrace` before analysing rather than assuming it was lost.

Attribute the segments separately with `--analyze <session dir>`. Do not try to merge
them: every number that matters is a share of thread time or a ms/frame rate, and both are
already comparable across segments. Two segments that disagree are telling you the scene
changed, which is worth reporting rather than averaging away.

### If target is `server`

Everything happens inside the `tmodloader` container. Do not write to the host, and do
not touch anything outside that container.

Check the container and the toolchain first:

```
ssh <host> "docker ps --filter name=tmodloader --format '{{.Names}}\t{{.Status}}'"
ssh <host> "docker exec tmodloader ls -la /root/dotnet-trace"
```

If `dotnet-trace` is missing, install it into the container before capturing.

The container has **no system .NET** — only the runtime bundled with the server. Every
invocation needs `DOTNET_ROOT`, or it fails with *"You must install .NET to run this
application"*:

```
ssh <host> "docker exec -e DOTNET_ROOT=/terraria-server/dotnet tmodloader \
  /root/dotnet-trace ps"
```

Picking the PID needs care — three processes look plausible and two are wrong:

- a `tmux` server, which matches a naive `pgrep -f tModLoader`
- a second `dotnet` running with **`-subworld`**, which is a SubworldLibrary subserver
- the real one: `dotnet tModLoader.dll -server` **without** `-subworld`

**The server only ticks while at least one player is connected** (`Main.DedServ` guards
`Update()` on `Netplay.HasClients`). A capture with nobody online records an idle loop and
proves nothing. Confirm players are connected before capturing, and say so if they are not.

Stream the result out rather than `docker cp`, which would write to the host:

```
ssh <host> "docker exec tmodloader cat /root/srv.nettrace" > <scratchpad>/srv.nettrace
```

Check the byte count matches the file inside the container before trusting it.

---

## Phase 2 — Attribute

```
~/.dotnet/tools/dotnet-trace convert <trace>.nettrace --format speedscope
python tools/attribute.py <trace>.speedscope.json
```

For a **server** trace, get the tick count first so the tool can report ms/tick, which is
the only unit comparable against the 16.67 ms budget. The `Main.DoUpdate` loop calls
`Thread.Sleep` exactly once per iteration, so counting its opens gives the tick count —
then re-run with `--ticks <n>`. Also record what fraction of the thread sits in
`Thread.Sleep`: that is the server's headroom, and if it is large the server is not the
problem no matter what the ranking says.

For a **client** trace, get the real rates rather than assuming 60: count opens of
`Main::DoDraw` and `Main::DoUpdate` and divide by the span. Draw and update run at
different rates, so `--ticks` is only right for one of them at a time — say which unit
every number is in. Then check `Microsoft.Xna.Framework.Game.Tick()`'s **self** time: that
is FNA's frame limiter idling. Near zero means the thread is saturated and every
millisecond saved becomes frame rate; a large share means the scene is not CPU-bound and
removing work will not show up as fps at all.

Descend with `--roots` into whatever dominates, one level at a time. Use `--callers` to
find who is responsible for an expensive framework call (`DateTime.get_Now`, `SetData`,
allocation-heavy paths). `tools/flamegraph.py` draws the whole thread at once with the
idle taken out, which is what you want when the per-frame costs found so far do not add up
to the budget; `tools/firefox_profile.py` exports a window for profiler.firefox.com when
you want its timeline and inverted call tree.

Five rules that override whatever the ranking suggests:

1. **A detour carries the inclusive time of everything inside it.** A mod can read 60% of
   the thread and cost nothing. Check its own `CPU_TIME` before blaming it.
2. **Self-time alone under-reports a wrapper.** A detour that adds work through its
   *children* — a batch restart, a LINQ pass — has almost no self time and still costs.
   Split its inclusive time into the child that continues to `orig` (a `SyncProxy`,
   `Hook<>` or `DMD<>` frame) and everything else. Everything else is what it added.
3. **Normalise self-time by call frequency before comparing detours.** Call counts differ
   by orders of magnitude, so a thin wrapper on a hot path outranks a fat one on a cold
   path. Ranking by raw self-time has produced a wrong conclusion here before.
4. **Weight by interval duration, never by sample count.** The speedscope conversion is
   evented: it records only stack *changes*, so a stack that survives ten sampling periods
   is one long interval, while the instant between one child returning and the next being
   called is an interval of a few microseconds. Counting intervals equally makes those
   instants look enormous — it once put `Main.DoDraw` at 30% self time when it is 0.1%.
5. **Cross-check a surprise against a second tool.** When two disagree, trust the one that
   sums real interval durations.

`CPU_TIME` and `UNMANAGED_CODE_TIME` are synthetic frames, not mods. They tell you where
the sampler stopped, not who is at fault. Dropping them puts their time on the deepest real
frame, which is the right attribution — but it means "self time" there includes native work
that frame called into, so a frame heavy in GPU or FNA calls looks like it is busy itself.

The worker threads are worth looking at **once** per investigation and usually no more:
pool them and you will find ~95% parked in `LowLevelLifoSemaphore` and `WaitHandle`. What
matters on the main thread is its wait in `FastParallel.For`, not what the workers do.

---

## Phase 3 — Formulate, then stop

For every candidate worth proposing, **decompile the owning mod and read the method.**

```
python tools/untmod.py <path-to>.tmod <outdir>            # extract the .tmod
ilspycmd -p -o src/ -r "<tml-install-dir>" -r <mods-dir> <outdir>/TheMod.dll
```

A profile says *where*. Only the source says *whether the work should happen at all*. Do
not propose a patch for a method you have not read — that has generated two false
findings in this project.

**Check the installed mods' own config before proposing anything.** Read the JSON in
`Documents/My Games/Terraria/tModLoader/ModConfigs/` and the defaults in the mod's config
class — an empty `{}` means every default applies. An optimisation someone already wrote
and left switched off beats anything written here, and costs nothing to turn on.
HighFPSSupport ships two of them defaulted to false.

### Shapes that have actually paid

These are what previous runs happened to find, in the scenes they happened to capture,
against one mod list. They are a head start, **not a checklist** — every one of them was
unknown until someone read a stack and asked why it was there. Work the profile first and
consult this list second; if you go looking only for these eight you will find only these
eight. When you find a ninth, add it here.

Each is a profile signature and the thing to check in the source. Most recur across mods,
so once one is found it is worth scanning the whole pack for the same shape.

- **Constant lookups on a hot path.** `Assembly.GetType(string)`, `GetProperty`, a
  reflective `GetValue`, `mod.Find<T>("Name")`, `ModContent.XType<T>()`. Shows up as
  `RuntimeAssembly.GetTypeCore` or dictionary churn under a small method. None of these
  handles change after mods finish loading: resolve once, or fold the id in as a constant.
- **Wall-clock reads at tick frequency.** `DateTime.Now` is a timezone conversion, not a
  clock read. The *first* call of a tick pays a cold cost the later ones do not, so
  patching one site mostly hands the cost to whichever caller then goes first — find every
  site with `--callers DateTime.get_Now` and patch them together or not at all.
- **Sprite batch restarts.** Every `End()`/`Begin()` is a full render-state application and
  a shader bind. Find them with `--callers SpriteBatch.PrepRenderState` and
  `--callers SpriteBatch.End`. A per-entity restart in a `PostDraw` or `PreDrawInWorld` is
  the classic case. Two related mistakes: `SpriteSortMode.Immediate` with a null `Effect`
  buys nothing and flushes per sprite, and handing a batch back in a different sort mode
  than it was borrowed in degrades the rest of that pass.
- **Producer running while its consumer is gated.** Render targets prepared every frame,
  overlays rebuilt on frames they are not drawn, instrumentation nobody reads, a feature
  whose config is off. Look at *where* the early-out sits — it is often after the
  expensive part rather than in front of it.
- **Bare-gate detours.** A body that is only `if (!flag) return orig(...)`. There is no
  work to remove; the cost is the trampoline. Removable only by removing the hook, which
  reorders it against every other mod's hooks — usually a bad trade. Say so rather than
  filing it again.
- **Collections rebuilt from empty.** `AddWithResize` → `set_Capacity` → `Array.Copy` →
  `Buffer._Memmove` in the profile means a list is regrowing every pass. Clearing the
  container drops the capacity with it; clearing the lists in place keeps it.
- **Results that are discarded.** `list.OrderBy(...).ToList();` with nothing assigned sorts
  and allocates for nothing. Free to delete once you have confirmed nothing reads it.
- **Fixed-array loops are not the cost.** Iterating 400 item slots costs about 0.004
  ms/frame and 6000 dust slots about 0.1 ms/tick in total. The time is in what mods hang
  off each iteration. Do not propose restructuring a vanilla loop.

### Finding shapes that are not on that list

Every entry above was found by one of these, not by pattern matching:

- **Draw the whole thread before querying it.** `--callers` and `--roots` only answer
  questions you already knew to ask. `tools/flamegraph.py` shows what is there, including
  the parts nobody suspected.
- **Rank every frame by own work, not by the ones you suspect.** Inclusive time flatters
  wrappers and self time flatters leaves; the interesting column is inclusive minus the
  pass-through to `orig`, computed for everything at once.
- **Treat a hot runtime or BCL frame as a lead, never as an answer.** `Buffer._Memmove`,
  `Effect.INTERNAL_applyEffect`, `Stopwatch.GetRawElapsedTicks`, `RuntimeAssembly.GetTypeCore`
  and `Dictionary` churn each sat near the top of a ranking looking like unavoidable
  framework cost. Every one of them had a mod or a vanilla defect behind it, found by
  running `--callers` on it. If you cannot name who is causing a framework frame, you have
  not finished.
- **Ask what has no business running in this scene.** A boss's renderer with no boss alive,
  dialogue with nobody talking, an intro screen for a feature switched off in its own
  config, timing instrumentation with no reader. This question found more than any
  signature did, and it needs no tooling — just reading a stack and being suspicious that
  the code is there at all.
- **Follow a number that feels wrong.** "That seems too expensive for what it does" has
  been right more often than the ranking. Chase it until you can explain the cost, and if
  the explanation is boring, say so and move on.

Classify each candidate:

- **Defect** — the producer runs while its consumer is gated, or client-only work
  (rendering, audio, screen coordinates, `Main.LocalPlayer`, `Main.myPlayer`) executes on
  a dedicated server. This is what Hodgepodge is for.
- **Overhead** — the work is needed and the cost is dispatch. Only patchable by changing
  *how* it is hooked, never by removing it. Gains are partial; say so.
- **Inherent** — real work the pack asks for. Not a patch target.
- **Settings** — fixable by a config option or a game setting. Record it, do not patch it.

Then present each as: measured cost, root cause with the source quoted, proposed patch,
risk, and what could silently break later. Rank by confidence, not by size.

**Stop here.** Ask which to implement. Do not write mod code before that answer.

---

## Phase 4 — Implement (approved patches only)

For each approved patch:

- One `ModSystem` per patch, independently toggleable in `ModConfig`. A patch that cannot
  be switched off cannot be measured.
- Check every `TryGotoNext` / reflection lookup. On a miss, log the patch ID at error level
  and disable **that patch alone**. Never throw — an exception here takes the game down.
- Prefer an `On_` detour over an `IL_` manipulator wherever it wraps cleanly. Narrower
  patches survive dependency updates longer.
- When a patch both adds and removes behaviour, order it so a partial failure is harmless:
  install the replacement first, verify it applied, and only then remove what it replaces.
- **Verify a removal actually happened.** `HookEndpointManager.Remove` drops a miss
  silently, so a rebuilt delegate that does not match leaves the work running twice and
  reports nothing. Count detours before and after with
  `MonoMod.RuntimeDetour.DetourManager.GetDetourInfo(method).Detours`, or check
  `Hook.IsApplied` where the mod kept the `Hook` in a field, and log an error if it did not
  drop. The failure is harmless but it is not free, and it is invisible without the check.
- Never reset a guard counter by leaving it static. A `_injected`-style field carries the
  previous load's value into a reload and turns the check into a permanent no-op.
- Record the target mod's exact version in the entry. IL anchors are not a stable API.

A patch to shared infrastructure — FNA, tModLoader, anything every mod draws through — is
not the same kind of change as a patch to one mod. Before proposing one, write a scan that
looks for what it would break across every installed mod (`tools/scan_flushstate.py` is the
worked example) and ship it **off by default** until a before/after exists. A scan narrows
a 74 mod pack to a reviewable handful; it does not prove the patch safe, and it holds for
the current mod list and no other. That scan's own patch is the reason it reads that way:
it passed, shipped off by default, and broke wall rendering the first time it was switched
on. Take a clean scan as permission to test the patch, never as permission to keep it.

Put the patch in the right project. There are two, deliberately:

- **`Hodgepodge/`** (`side = Client`) — anything the server neither needs nor knows about.
  Client-side mods are excluded from `ModNet.SyncMods`, so this one installs and updates
  without touching the server or its mod list. Subfolder per target mod, e.g.
  `Hodgepodge/calamityhunt/`.
- **`HodgepodgeServer/`** (`side = Both`) — patches to server-side behaviour. Costs a
  server-side install and a restart to update, so only put a patch here when it genuinely
  has to run on the server.

Then update `README.md`: add the entry with the before number, and mark the after number as
**owed** until it is measured. Re-run this command to collect it. An entry without a
before/after pair is a claim, not a result.

Do not run any git command.
