# Hodgepodge

Hodgepodge is a tModLoader patch mod which fixes defects in other mods and removes work whose
result nobody sees. Every patch looks for its target mod at load and does nothing when that mod
is absent, so the same build is safe to run against any set of mods. Name copied from the NH equivalent.

It ships as two mods, one for the client and one for the server. This is mainly to allow having the
client optimizations compatible with servers who might not want to install the server side. For setups
where that is not an issue, the server mod can also be added for further improvements.

To build, run `./build.sh` to get compiled artifacts for both variants.

Every patch is a single `ModSystem` that can be switched off in config. When a member is missing
or a method body has changed, the patch logs an error and disables itself without touching the
rest of the mod.

## Patches

| Patch | Targets | Side | What it does |
|---|---|---|---|
| `TileEdgeHighlightGuard` | CalamityHunt | Client | Stops the tile edge overlay being rebuilt on frames it is not drawn. |
| `ConstellationStarBuffer` | NoxusBoss | Client | Sizes the star buffer to the stars in use rather than to its capacity. |
| `PlayerPostProcessingRedraw` | NoxusBoss | Client | Redraws a player into a shader target only when a shader asked for one. |
| `ForegroundDoubleUpdate` | Calamity | Client | Steps foreground effects once per tick instead of once per tick and once per frame. |
| `AprilFoolsDateCheck` | CalValEX | Client | Skips the April Fools date work outside single player, where it cannot apply. |
| `FullBrightDispatch` | SOTS | Client | Inlines the fullbright test and drops the detour around it. This one is off by default. |
| `LostColosseumSceneCheck` | UnCalamityModMusic | Client | Tests the biome flag before building a throwaway NPC to read a kill count from. |
| `ExoMechSkyLookup` | InfernumModeMusic | Client | Resolves a Calamity sky property once at load instead of once per tick. |
| `LootBeamBatchMode` | LootBeams | Client | Draws loot beams in a deferred batch, and hands the item pass back in the sort mode it borrowed. |
| `WallClockDateCache` | Thorium, CalValEX, Ragnarok | Client | Answers per tick `DateTime.Now` reads from a value refreshed once a second. |
| `SubworldActiveScan` | Calamity | Client | Asks SubworldLibrary once instead of once per loaded mod. |
| `ForgeRecipeConditionDispatch` | NoxusBoss | Client | Inlines the Starlit Forge adjacency test into the recipe checks. Off by default. |
| `NearbyEffectsBlockDispatch` | MagicStorage, SerousCommonLib | Client | Moves the pylon scanning test into `TileLoader.NearbyEffects` and drops both detours. |
| `TileLightProjectileBonus` | StarsAbove | Client | Replaces three per tile lighting detours with one reading a per tick value. Off by default. |
| `PolarityPulseBatchGuard` | Calamity | Client | Skips two per-NPC sprite batch restarts when that NPC has no pulses to draw. |
| `WorkshopTileCenterReuse` | UnCalamityModMusic | Client | Empties the tile centre lists in place once per scan instead of regrowing them from empty. |
| `DrawTimeLogging` | vanilla | Client | Stops `TimeLogger` timing every draw phase when nothing is recording a log. |
| `TilePreDrawContentIds` | SOTS | Client | Folds the per-tile `ModContent` id lookups in `PreDraw` into constants. |
| `BossIntroScreenGate` | InfernumMode | Client | Skips the boss intro screen draw loop while Infernum's own setting is off. |
| `MusicEventTrackLookup` | UnCalamityModMusic | Client | Resolves a Calamity property once at load instead of by name every tick. |
| `DebuffProjectileScan` | SOTS | Client | Skips dead projectile slots in a per NPC per tick scan whose every test needs a live one. |
| `ShaderDrawerSortDiscarded` | NoxusBoss | Client | Drops a per-frame sort whose result is never assigned. |
| `CurseDustPixelCache` | SOTS | Client | Reads the Pharaoh's Curse dust textures back off the GPU once instead of five times a tick. |
| `LingeringFieldParticleBroadcast` | StarsAbove | Client | Spawns three damage fields' particles locally instead of routing every one through the server. |
| `LingeringFieldParticleVisibility` | StarsAbove | Client | Skips those particles for fields off screen, where they are never drawn but still hold a pool slot. |
| `ParticlePoolScanCursor` | vanilla | Client | Resumes the search for a free particle where the last one succeeded instead of restarting at the front. Off by default. |
| `CurseIconDrawGate` | SOTS | Client | Skips the nine curse icon texture lookups on an NPC carrying no SOTS curse. |
| `LootBeamTextureCache` | LootBeams | Client | Answers the beam and glow texture lookups from a table instead of by name every frame. |
| `WhipBuffImmunityScan` | CalamitySimpleWhipAddon | Client | Clears immunity for the mod's own buffs from a list built once instead of walking every buff type per NPC per tick. |
| `BlueMoonBuffLookup` | InfernalEclipseAPI | Client | Resolves the BlueMoon buff handles once at load instead of by name per player per tick. |
| `AstrageldonPresenceScan` | CalamityHunt | Client | Tests the NPC type before scanning the world, and drops a second scan whose result is never read. |
| `SceneEffectWeightCapacity` | tModLoader | Client | Gives the per tick scene effect weight list a capacity so it stops regrowing from empty. Off by default. |
| `InfernalItemBalanceCache` | InfernalEclipseAPI | Client | Answers the item balance helper's `(mod, name)` question from a table instead of four dictionary lookups per call. |
| `ModTypeNameCache` | Daybreak | Client | Answers `ModType.Name` from a cache instead of re-deriving it through reflection on every read. Off by default. |
| `InfernalBossBuffScan` | InfernalEclipseAPI | Client | Asks whether an NPC is a boss before walking 255 player slots for it rather than after. |
| `TimeFrozenIdLookup` | InfernalEclipseAPI | Client | Folds the NPC ids a per NPC per tick check rebuilds by name into constants. |
| `ResetEffectsContentIds` | Calamity | Client | Folds the 51 `ModContent.NPCType` lookups in `ResetEffects` into constants. |
| `VoidAnomalyRangeGuard` | SOTS | Client | Skips the Void Anomaly's per entity work outside the reach at which either helper can act. |
| `LumenylCrystalScan` | InfernumMode | Client | Narrows a 32,000 tile per frame Abyss crystal sweep to the tiles on screen. Off by default. |
| `MusicFlagsUpdate` | UnCalamityModMusic | Server | Stops the server deriving music state for a player that is not there. |
| `ForegroundRainUpdate` | Calamity | Server | Stops the server simulating foreground rain it never draws. |
| `AprilFoolsTextureCheck` | CalValEX | Server | Stops the April Fools texture test running per NPC per tick, where it can never be true. |
| `BlueMoonBuffLookup` | InfernalEclipseAPI | Server | Resolves the BlueMoon buff fields once at load instead of by name per player per tick. |
| `WallClockDateCache` | Thorium, CalValEX, Ragnarok | Server | Answers per tick `DateTime.Now` reads from a value refreshed once a second. |
| `XykWingCountScan` | Calamity | Server | Counts a player's Xyk wings once per wing update instead of four times. |
| `RepeatedTextFormat` | vanilla | Server | Answers a repeated `LocalizedText.Format` from the previous result when nothing changed. |
| `DebuffProjectileScan` | SOTS | Server | Skips dead projectile slots in a per NPC per tick scan whose every test needs a live one. |

## Measurements

### Client

Ten minutes of underground mining, 63 fps, 15.89 ms/frame against a 16.67 ms budget, so the main
thread has no idle headroom and anything reclaimed here becomes frame rate.

| Patch | Before | After |
|---|---|---|
| `DebuffProjectileScan` | 0.327 ms/tick, 1.96% of the thread — 96% of it the method's own inlined loop | owed |
| `LostColosseumSceneCheck` | 0.141 ms/tick, 0.88% of the thread | owed |
| `ExoMechSkyLookup` | 0.105 ms/tick, 0.66% of the thread | owed |
| `LootBeamBatchMode` | 0.235 ms/frame of `SpriteBatch.PrepRenderState`, plus an unmeasured share of the 0.668 ms/frame in implicit flushes and of the 0.447 ms/frame the item pass spends in `SpriteBatch.Draw` while stuck in immediate mode | the item pass no longer flushes per sprite: vanilla's `SpriteBatch.Draw` inside `Main.DrawItem` is 0.001 ms/frame against 0.447 before, the three beam draws are 0.011 ms/frame, and neither `SpriteBatch.Draw` nor `DrawItem` appears as a `FlushBatch` caller at all. What the patch cannot reach is the restart itself: `StartAdditive` and `StopAdditive` are 0.850 + 0.354 ms/frame, all of it `SpriteBatch.End` flushing the item pass, because an `End`/`Begin` pair costs that in any sort mode. Only LootBeams' own `DrawAdditive = false` removes it, at the cost of additive beams |
| `WallClockDateCache` | 0.198 ms/tick, 1.19% — Thorium 0.117, CalValEX 0.045, Ragnarok 0.030 | owed |
| `SubworldActiveScan` | 0.050 ms/tick, 0.30% | owed |
| `ForgeRecipeConditionDispatch` | 0.130 ms/tick, 0.82% | owed |
| `NearbyEffectsBlockDispatch` | 0.88% of the client thread (0.59% MagicStorage + 0.29% SerousCommonLib) | owed |
| `TileLightProjectileBonus` | 3.69% / 4.94% of a lighting worker thread across two captures; the share reaching the main thread's `FastParallel` wait is unknown | owed |
| `PolarityPulseBatchGuard` | 0.098 ms/frame, 0.68% — 98.4% of it `SpriteBatch.End`, 0.0% its own code | owed |
| `WorkshopTileCenterReuse` | 0.153 ms/frame, 1.06% — 85.3% `List<Vector2>` growth, 12.8% dictionary churn, 0.0% its own code | the first version cost 4.871 ms/frame, 22.44% of the client thread over a 120 s capture at 46 fps, because emptying at the call site it replaced runs once per wide-area tile where `Dictionary.Clear` short circuits after the first. Now emptied once per scan in `ScanAndExportToMain`; re-measurement owed |
| `DrawTimeLogging` | 0.107 ms/frame, 0.75% in `Stopwatch.GetRawElapsedTicks` alone | owed |
| `TilePreDrawContentIds` | 0.032 ms/frame of the 0.068 ms/frame `SOTSTile.PreDraw` costs | owed |
| `BossIntroScreenGate` | 0.077 ms/frame, 0.55%, with the feature switched off in Infernum's config. Only the draw loop is reclaimable, 324 ms of the 882 ms the method cost over a separate 240 s capture; `UpdateScreens` has to keep running because it holds the animation reset | owed |
| `MusicEventTrackLookup` | 0.31% of the thread; 36% of everything `MusicFlags.PreUpdate` costs | owed |
| `ShaderDrawerSortDiscarded` | part of the 0.31% own-work in `DrawInterfaceProjectiles` | owed |
| `CurseDustPixelCache` | 0.470 ms/frame, 1.77% of the client thread, of which 0.426 is `Texture2D.GetData` alone. A GPU readback also stalls the pipeline outside managed frames, so that is a floor. Only costs anything while a Pharaoh's Curse is alive | owed |
| `LingeringFieldParticleBroadcast` | the receive path, `NetMessage.CheckBytes`, was 44.06% of the client thread over a 60 s capture with Thespian's fields alive, against StarsAbove 2.1.8.4 | 0.46%. The round trip is gone. This does **not** show up as frame rate: what that 44% was actually spending its time in is `ParticlePool.RequestParticle`, which the patch does not touch, so removing the packets only moves the same scan onto the local half. The client drew 29 frames in the 60 s before and 2 in the 60 s after, both scenes fully saturated by the scan and neither a controlled comparison. Worth keeping for the traffic it removes group-wide, not for fps |
| `LingeringFieldParticleVisibility` | part of the 86.45% below: the arena holds far more fields than fit on screen, and each off screen one still takes a pool slot per tick. What share is off screen is scene dependent and was not measured | owed |
| `ParticlePoolScanCursor` | 86.45% of the client thread, 51.9 s of a 60 s capture in `ParticlePool.RequestParticle`, of which 86.44% is the scan loop itself and only 5.2 ms reaches `FetchFromPool`. The pool is not growing — no instantiator frame appears — so every request is finding a free slot, just after walking a long prefix of in-use ones. Main thread drew 2 frames in those 60 seconds | owed |

A second capture, sixty seconds of a boss fight with the patches above installed. 2204 frames at
36.7 fps, 27.23 ms/frame, `Game.Tick` self time 0.67% and the thread 97.4% busy, so this scene has
no idle headroom either. Update and draw both run at 36.7/s because HighFPSSupport ties them
together, which makes ms/frame and ms/tick the same unit here.

| Patch | Before | After |
|---|---|---|
| `LootBeamTextureCache` | 0.703 ms/frame, 2.58% of the thread for the whole of `PreDrawInWorld`, of which 0.65 is `AssetRepository.Request` and the drawing is the remainder | owed |
| `CurseIconDrawGate` | 0.552 ms/frame, 2.03% — the whole of `DebuffNPC.PostDraw`, every millisecond of it the nine texture lookups and none of it drawing | owed |
| `SceneEffectWeightCapacity` | 0.294 ms/frame, 1.08%, all of it inside `List.AddWithResize` and `set_Capacity`. The number is larger than a handful of small array copies should cost, so some of what lands there is likely allocation and collection charged to the frame that allocated, which makes the size of the win uncertain even though the regrowth is real | owed |
| `WhipBuffImmunityScan` | 0.260 ms/frame, 0.95% — effectively all of it the mod's own inlined loop over `BuffLoader.BuffCount` | owed |
| `BlueMoonBuffLookup` | 0.240 ms/frame, 0.88%, of which `UpdateFloralBlessing` alone is 491 ms of the capture. The server mod already carried this patch and gates it behind `Main.dedServ`, so on a client the cost stood unclaimed | owed |
| `AstrageldonPresenceScan` | 0.113 ms/frame, 0.42% — 137 ms in the discarded `FindFirstNPC` scan, 61 ms in `AnyNPCs`, the rest rebuilding the mod and NPC handles by name | owed |

Together these are about 1.87 ms of the 27.23 ms frame without the tModLoader one, near 7% and
something like 2.5 fps at that rate.

Three things in the same capture were looked at and not patched. The largest unclaimed cost in it
is `InfernalEclipseAPI.InfernalItemBalanceChange.SetDefaults` at 0.469 ms/frame, 1.72%, driven by
item sync through `NetMessage.CheckBytes`: a single 4302 line method walking hundreds of
`ModContent.ItemType<T>()` and `mod.Find<ModItem>(name)` lookups linearly for every item that gets
defaults. There is no patch of a reasonable size for a method that long and memoising it would mean
second-guessing what it writes, so it belongs upstream. `NPCLoader.PostAI` carries 1.82 ms/frame of
its own dispatch and `ProjectileLoader.CanHitNPC` 0.40 ms/frame, both entirely loader iteration
over globals, which is inherent to the pack size. FancyLighting reads 41% of the thread inclusive
and 0.001 ms/frame of its own, because every one of its options is switched off in its config,
which makes its `DoDraw` hook a bare trampoline and not something a patch can reach.

`ParticlePoolScanCursor` is the second patch here to shared infrastructure, and it ships off by
default for the reason the paragraph below gives. `tools/scan_particlepool.py` narrowed the 77
enabled mods to one that could notice: CalamityHunt requests from `ParticlePool<T>` with its own
`IPooledParticle`, so it would see its particles reused and drawn in a different order within a
pool. No mod reads a pool's backing list directly. That is permission to test it, not to keep it.

There was an `EmptyBatchFlush` here, which skipped `PrepRenderState` for a sprite batch that
flushed with nothing queued. It shipped off by default behind a survey of what inherits render
state from someone else's flush (`tools/scan_flushstate.py`), and it drew walls at the wrong
screen offset the first time it was switched on with FancyLighting installed. It is removed
rather than narrowed: an empty `End` that stops touching the device is only safe against a fixed
mod list, so every mod added re-opens the question, and the win was never measured in the first
place. The scan stays as the worked example for the next patch to shared infrastructure.

Rerun `/profile client 600` in the mining scene, and `/profile client 60` in a comparable boss
fight, to collect the after numbers each table is owed. Until then every row above is a claim, not
a result.

### Client, lower-end setup

Three 120 second captures from a second machine, in an idle scene, a blood moon and a boss fight,
with both mods installed and a client build predating `DebuffProjectileScan` and everything below
it in the table above. `Game.Tick` own time is the frame limiter idling, so it is the headroom
left: 3.3% idle, 1.8% boss, 1.2% blood moon. All three scenes are CPU bound.

| Scene | fps | ms/frame | in-world updates/s | 1% of the thread |
|---|---|---|---|---|
| idle | 54.4 | 18.38 | 57.9 | 0.184 ms/frame |
| boss | 31.3 | 31.91 | 51.1 | 0.319 ms/frame |
| blood moon | 17.9 | 56.03 | 34.0 | 0.560 ms/frame |

The blood moon is the scene worth having. It is the only capture here that is update bound rather
than draw bound, at 75.6% of the thread in `DoUpdate` against 22.2% in `DoDraw`, and the only one
running catch-up ticks. One player's `Player.Update` costs 4.07 ms idle, 4.47 ms in the boss fight
and 5.48 ms in the blood moon, which is a quarter of a 16.67 ms budget for one player standing
still.

Before numbers for the patches added from these captures. None has an after number yet.

| Patch | Before | After |
|---|---|---|
| `InfernalItemBalanceCache` | 0.94 ms/frame blood moon — `GetItem` 1.55% of the thread and `UnsafeGetItem` 0.13%, effectively all of it dictionary lookups, inside a `SetDefaults` costing 2.77%. Driven by item sync through `NetMessage.CheckBytes`, which is 3.91% of that thread. 0.50% boss, 0.41% idle | owed |
| `ResetEffectsContentIds` | 0.26 ms/frame blood moon — 0.47% of the thread inside `ModContent.NPCType` alone, of the 1.37% the method costs. 51 lookups per NPC per tick | owed |
| `InfernalBossBuffScan` | 0.76 ms/frame blood moon (1.36%, of which 1.34% is the method's own body). 0.52% boss, 0.34% idle. The gap between scenes is the point: the cost scales with NPCs alive, not with bosses alive | owed |
| `TimeFrozenIdLookup` | 0.73 ms/frame blood moon (1.30%), 0.45% boss, 0.31% idle. Seven ids rebuilt by name per NPC per tick | owed |
| `ModTypeNameCache` | 0.58 ms/frame blood moon (1.04%), 0.51% boss, 0.40% idle, with 1.41% of the blood moon thread inside `RuntimeType.InitializeCache` beneath it because the runtime drops that cache on collection. Part of this traffic is removed by `InfernalBossBuffScan` regardless, so the two have to be measured together | owed |
| `VoidAnomalyRangeGuard` | 0.31 ms/frame boss (0.98%), 0.88% blood moon, 0.71% idle. Flat across every scene, which is what a cost that never stops looks like | owed |
| `LumenylCrystalScan` | 0.17 ms/frame boss (0.54%), 0.57% idle, 0.30% blood moon. Found nothing in any of the three, because the tile it looks for only generates in the Abyss | owed |

These captures also carry before numbers for patches that shipped earlier but were not in this
build, measured on hardware where they cost two to five times what the high-end traces showed.
They are before numbers, not after numbers for anything.

| Patch | Target cost here | Earlier before |
|---|---|---|
| `DebuffProjectileScan` | 1.76 ms/frame blood moon (3.15%) | 0.327 ms/tick |
| `SceneEffectWeightCapacity` | 1.06 ms/frame blood moon (1.90%) | 0.294 ms/frame |
| `LootBeamTextureCache` | 0.61 ms/frame boss (1.90%) | 0.703 ms/frame |
| `AstrageldonPresenceScan` | 0.72 ms/frame blood moon (1.29%) | 0.113 ms/frame |
| `CurseIconDrawGate` | 0.67 ms/frame blood moon (1.19%) | 0.552 ms/frame |
| `WhipBuffImmunityScan` | 0.44 ms/frame blood moon (0.78%) | 0.260 ms/frame |
| `BlueMoonBuffLookup` | 0.36 ms/frame boss (1.14%) | 0.240 ms/frame |

Three things in these captures were looked at and not patched. `NPCLoader.PostAI` is the largest
single item in the blood moon trace at 12.25% inclusive and 7.01% of its own, which is 3.93
ms/frame of loader dispatch over the pack's globals; it scales with NPC count and there is nothing
in it to remove. StructureHelper detours `DrawInterface`, `DrawPlayers_AfterProjectiles` and
`CheckMonoliths`, carrying 7.34%, 4.85% and 1.26% inclusive, and every one of them has 0.00% own
time, so they are bare trampolines. Two vanilla costs are recorded rather than proposed, because
both are shared infrastructure and would need a scan first: `TeleportPylonsSystem.IsPlayerNearAPylon`
runs a LINQ `Any` with an interaction-range tile scan per pylon per map draw at 0.52% to 0.95%, and
`MapHeadRenderer.PrepareRenderTarget` re-renders every connected player's outlined head every frame
at 0.68% to 1.02%.

### Server

Thirty seconds of a six player boss fight with none of these patches installed. 1789 ticks at 59.5
tps, 9.02 ms of work per tick against a 16.67 ms budget, and 46.3% of the thread parked in
`Thread.Sleep`. Unlike the client the server has headroom, so nothing reclaimed here becomes tick
rate — it becomes margin against the next pack that costs more.

| Patch | Before | After |
|---|---|---|
| `AprilFoolsTextureCheck` | 0.671 ms/tick, 3.99% — 96% of everything `NPCLoader.FindFrame` cost | owed |
| `MusicFlagsUpdate` | 0.330 ms/tick, 1.96% | owed |
| `BlueMoonBuffLookup` | 0.329 ms/tick, 1.96% — 0.308 of it inside `Assembly.GetType` | owed |
| `DebuffProjectileScan` | 0.290 ms/tick, 1.73% — 96% of it the method's own inlined loop | owed |
| `ForegroundRainUpdate` | 0.245 ms/tick, 1.46% | owed |
| `WallClockDateCache` | 0.183 ms/tick, 1.09% — Thorium 0.157, CalValEX 0.026 | owed |
| `XykWingCountScan` | 0.105 ms/tick, 0.62%, of which the patch removes three scans in four | owed |
| `RepeatedTextFormat` | 0.071 ms/tick, 0.42% — 71% of everything `Player.UpdateArmorSets` cost | owed |

`BlueMoonBuffLookup` and `XykWingCountScan` are the two here that reimplement another mod's method
rather than gate one, so neither can be pinned by inspecting IL the way the others are. They were
read against InfernalEclipseAPI 2026.7 and CalamityMod 2026.6, and a change to what those methods
do would need them switched off rather than caught at load.

Rerun `/profile server 60` during a comparable fight to collect the after numbers.
