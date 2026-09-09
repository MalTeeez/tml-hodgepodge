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
| `MusicFlagsUpdate` | UnCalamityModMusic | Server | Stops the server deriving music state for a player that is not there. |
| `ForegroundRainUpdate` | Calamity | Server | Stops the server simulating foreground rain it never draws. |
