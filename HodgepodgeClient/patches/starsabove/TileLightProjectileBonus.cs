using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Graphics.Light;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// StarsAbove registers three separate detours on TileLightScanner.GetTileLight, one per background
// projectile, each of them the same shape:
//
//     originalLight.Invoke(self, x, y, ref outputColor);
//     if (Main.LocalPlayer.ownedProjectileCounts[ModContent.ProjectileType<XBackground>()] >= 1)
//         outputColor += Vector3.One * 0.01f;
//
// GetTileLight is the hottest per tile method in the game, so that is three detour trampolines and
// three ModContent.ProjectileType lookups for every tile of every lighting pass, to notice a
// projectile that exists during one boss fight. It is the top mod on the lighting worker threads in
// both captures -- 3.69% of a worker in one, 4.94% in the other -- and the lighting scan is what
// the main thread blocks on in FastParallel.For, so it sits on that critical path rather than
// running free beside it.
//
// The answer does not vary per tile, only per tick, so the counts are read once in
// PostUpdateEverything and every tile reads one float. Three trampolines become one.
//
// The gain is bounded by the main thread's wait on the lighting pass rather than by the worker
// share itself.
public class TileLightProjectileBonus : Patch
{
    private const string TargetMod = "StarsAbove";
    private const float BonusPerBackground = 0.01f;

    // The ModSystem, its detour, and the background projectile that system watches -- the only
    // thing that differs between the three.
    private static readonly (string System, string Method, string Projectile)[] Sources =
    [
        ("SpacePowerLighting", "Light", "SpacePowerBackground"),
        ("UnlimitedBladeWorksLighting", "UBWLight", "UnlimitedBladeWorksBackground"),
        ("CallOfTheStarsLighting", "Light", "CallOfTheStarsBackground"),
    ];

    private static int[] _backgroundTypes = [];
    private static float _bonus;

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().TileLightProjectileBonus;

    // Written once a tick on the main thread and read from the lighting workers. A stale read costs
    // one frame of a 0.01 brightness step, which is the same thing a tick of latency already costs.
    public override void PostUpdateEverything()
    {
        float bonus = 0f;
        foreach (int type in _backgroundTypes)
        {
            if (Main.LocalPlayer.ownedProjectileCounts[type] >= 1)
                bonus += BonusPerBackground;
        }

        _bonus = bonus;
    }

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out _))
            return;

        FieldInfo ownedProjectileCounts =
            typeof(Player).GetField(nameof(Player.ownedProjectileCounts));
        List<(ModSystem System, MethodInfo Detour)> replacing = [];
        List<int> backgrounds = [];

        foreach ((string systemName, string methodName, string projectileName) in Sources)
        {
            if (!ModContent.TryFind(TargetMod, systemName, out ModSystem system)
                || !ModContent.TryFind(TargetMod, projectileName, out ModProjectile background))
            {
                Mod.Logger.Error($"Tile light projectile bonus: {systemName} or {projectileName} " +
                    $"is missing from {TargetMod}, patch disabled");
                return;
            }

            MethodInfo detour = system.GetType().GetMethod(methodName, BindingFlags.Instance
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            // Replicating these means owning them, and the whole reason one float can stand in for
            // all three is that each only ever asks whether a projectile is owned.
            if (detour == null || !ReadsField(detour, ownedProjectileCounts))
            {
                Mod.Logger.Error($"Tile light projectile bonus: {systemName}.{methodName} is no " +
                    "longer a bare owned projectile check, patch disabled");
                return;
            }

            replacing.Add((system, detour));
            backgrounds.Add(background.Type);
        }

        _backgroundTypes = [.. backgrounds];

        // Install the replacement before removing anything, so a failed detach leaves the bonus
        // applied twice rather than not at all.
        On_TileLightScanner.GetTileLight += AddBackgroundBonus;

        MethodInfo getTileLight = typeof(TileLightScanner).GetMethod(
            nameof(TileLightScanner.GetTileLight));
        foreach ((ModSystem system, MethodInfo detour) in replacing)
        {
            Detach<On_TileLightScanner.hook_GetTileLight>(getTileLight, system, detour,
                handler => On_TileLightScanner.GetTileLight -= handler);
        }
    }

    private static void AddBackgroundBonus(On_TileLightScanner.orig_GetTileLight orig,
        TileLightScanner self, int x, int y, out Vector3 outputColor)
    {
        orig(self, x, y, out outputColor);
        if (_bonus > 0f)
            outputColor += Vector3.One * _bonus;
    }
}
