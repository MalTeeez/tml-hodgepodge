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
| `FullBrightDispatch` | SOTS | Client | Inlines the fullbright test and drops the detour around it. |
| `LostColosseumSceneCheck` | UnCalamityModMusic | Client | Tests the biome flag before building a throwaway NPC to read a kill count from. |
| `ExoMechSkyLookup` | InfernumModeMusic | Client | Resolves a Calamity sky property once at load instead of once per tick. |
| `LootBeamBatchMode` | LootBeams | Client | Draws loot beams in a deferred batch, and hands the item pass back in the sort mode it borrowed. |
| `WallClockDateCache` | Thorium, CalValEX, Ragnarok | Client | Answers per tick `DateTime.Now` reads from a value refreshed once a second. |
| `SubworldActiveScan` | Calamity | Client | Asks SubworldLibrary once instead of once per loaded mod. |
| `ForgeRecipeConditionDispatch` | NoxusBoss | Client | Inlines the Starlit Forge adjacency test into the recipe checks. Off by default. |
| `NearbyEffectsBlockDispatch` | MagicStorage, SerousCommonLib | Client | Moves the pylon scanning test into `TileLoader.NearbyEffects` and drops both detours. |
| `TileLightProjectileBonus` | StarsAbove | Client | Replaces three per tile lighting detours with one reading a per tick value. |
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
| `SceneEffectWeightCapacity` | tModLoader | Client | Gives the per tick scene effect weight list a capacity so it stops regrowing from empty. |
| `InfernalItemBalanceCache` | InfernalEclipseAPI | Client | Answers the item balance helper's `(mod, name)` question from a table instead of four dictionary lookups per call. |
| `ModTypeNameCache` | Daybreak | Client | Answers `ModType.Name` from a cache instead of re-deriving it through reflection on every read. Off by default. |
| `InfernalBossBuffScan` | InfernalEclipseAPI | Client | Asks whether an NPC is a boss before walking 255 player slots for it rather than after. |
| `TimeFrozenIdLookup` | InfernalEclipseAPI | Client | Folds the NPC ids a per NPC per tick check rebuilds by name into constants. |
| `ResetEffectsContentIds` | Calamity | Client | Folds the 51 `ModContent.NPCType` lookups in `ResetEffects` into constants. |
| `VoidAnomalyRangeGuard` | SOTS | Client | Skips the Void Anomaly's per entity work outside the reach at which either helper can act. |
| `LumenylCrystalScan` | InfernumMode | Client | Narrows a 32,000 tile per frame Abyss crystal sweep to the tiles on screen. |
| `CurseFoamDrawGate` | SOTS | Client | Skips the foam texture lookup when there is no foam to draw, caches it otherwise, and folds the per slot projectile ids. |
| `NebulaFoamDrawGate` | CatalystMod | Client | The same fix for Catalyst's copy of the same helper. |
| `PlasmaGenBossScan` | InfernalEclipseAPI | Client | Answers "is a blocking boss alive" once per tick instead of seventeen world scans per accessory per player. |
| `BossPromptPresenceScan` | StarsAbove | Client | Runs the boss prompt checks only on ticks where one of their bosses is alive, from one scan per tick. |
| `NuclearTorrentInvisibleDraw` | Calamity | Client | Skips the torrent and rain draws and the raindrop simulation while faded out and not showing, and caches the raindrop texture. |
| `SeaPrismPresenceScan` | Calamity | Client | Skips the sea prism tile and wall shader passes, and their batch restarts, when no prism is on screen. |
| `EmptyPixelationTargetDraw` | Calamity, Luminance | Client | Skips the fullscreen draw of a pixelation target nothing drew into this frame. |
| `MusicFlagsLocalPlayer` | UnCalamityModMusic | Client | Derives the music flags for the local player only, which is all the method ever read. |
| `CritterBestiaryRegistration` | CalValEX | Client | Registers a nearby critter from its content sample, and only until its entry is fully unlocked. |
| `XykWingCountScan` | Calamity | Client | Counts a player's Xyk wings once per wing update and once per owner per draw, and caches the wing textures. |
| `PlayerDrawTextureCache` | Calamity | Client | Answers the tail and backpack texture lookups from a table, and folds the backpack item ids. |
| `AuricSoulSceneItemIds` | CalamityHunt | Client | Folds the item id each soul scene resolves per item slot per tick into a constant. |
| `StellarNovaTextGate` | StarsAbove | Client | Builds the Stellar Nova panel's seven strings only while the panel can be seen. |
| `CosmosMetaballIdleDraw` | CalamityHunt | Client | Skips the black hole shade's render target and fullscreen shader pass while nothing would draw into it. |
| `HeldItemSnapshotClone` | vanilla | Client | Keeps the held item clone draw code reads while its type, stack and prefix are unchanged, vanilla's own test during item use. |
| `MouseItemSlotClone` | vanilla | Client | Keeps the clone in temporary held-item slot 58 while the mouse item's type, stack and prefix are unchanged. |
| `MusicFlagsUpdate` | UnCalamityModMusic | Server | Stops the server deriving music state for a player that is not there. |
| `ForegroundRainUpdate` | Calamity | Server | Stops the server simulating foreground rain it never draws. |
| `AprilFoolsTextureCheck` | CalValEX | Server | Stops the April Fools texture test running per NPC per tick, where it can never be true. |
| `BlueMoonBuffLookup` | InfernalEclipseAPI | Server | Resolves the BlueMoon buff fields once at load instead of by name per player per tick. |
| `WallClockDateCache` | Thorium, CalValEX, Ragnarok | Server | Answers per tick `DateTime.Now` reads from a value refreshed once a second. |
| `XykWingCountScan` | Calamity | Server | Counts a player's Xyk wings once per wing update instead of four times. |
| `RepeatedTextFormat` | vanilla | Server | Answers a repeated `LocalizedText.Format` from the previous result when nothing changed. |
| `DebuffProjectileScan` | SOTS | Server | Skips dead projectile slots in a per NPC per tick scan whose every test needs a live one. |