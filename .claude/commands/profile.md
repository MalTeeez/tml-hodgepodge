---
description: Profile the Terraria client or server, attribute hotspots, propose patches, implement approved ones
argument-hint: [client|server] [seconds, default 60]
allowed-tools: Bash, Read, Write, Edit, Glob, Grep
---

Run the full Hodgepodge loop against **$1** for **$2** seconds (default 60 if unset).

Work through the phases in order. **Stop at the end of phase 3 and wait for approval.**
Phase 4 runs only on explicit confirmation of specific patches.

---

## Phase 1 — Capture

Write the trace to the scratchpad, never into this repo.

### If target is `client`

The game must already be running and **in the scene that hurts** before you start. If it
is not running, say so and stop — do not capture an idle menu.

```
~/.dotnet/tools/dotnet-trace ps | grep -i tmodloader
~/.dotnet/tools/dotnet-trace collect -p <pid> --buffersize 512 \
  --duration 00:00:01:00 --output '<WINDOWS-STYLE PATH>\client.nettrace'
```

Three traps, all of which have already cost a run:

- **Do not pass `--profile cpu-sampling`.** This dotnet-trace version rejects it with
  *"does not apply to `dotnet-trace collect`"*. Omitting `--profile` gives CPU sampling
  plus the runtime provider, which is what is wanted.
- `--duration` is `dd:hh:mm:ss`. 60 seconds is `00:00:01:00`, not `00:00:60`.
- The `--output` path must be a **Windows** path. A Git Bash `/tmp/...` path silently
  resolves somewhere the tool cannot write and the file never appears.

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

Descend with `--roots` into whatever dominates, one level at a time. Use `--callers` to
find who is responsible for an expensive framework call (`DateTime.get_Now`, `SetData`,
allocation-heavy paths).

Two rules that override whatever the ranking suggests:

1. **A detour carries the inclusive time of everything inside it.** A mod can read 60% of
   the thread and cost nothing. Check its own `CPU_TIME` before blaming it.
2. **Normalise self-time by call frequency before comparing detours.** Call counts differ
   by orders of magnitude, so a thin wrapper on a hot path outranks a fat one on a cold
   path. Ranking by raw self-time has produced a wrong conclusion here before.

`CPU_TIME` and `UNMANAGED_CODE_TIME` are synthetic frames, not mods. They tell you where
the sampler stopped, not who is at fault.

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
- Record the target mod's exact version in the entry. IL anchors are not a stable API.

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
