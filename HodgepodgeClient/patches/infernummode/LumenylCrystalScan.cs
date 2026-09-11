using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// TileLightingSystem.UpdateUI sweeps a fixed box around the player once per frame, looking for one
// Abyss tile so it can queue it for drawing on top of Infernum's blur:
//
//     for (int i = -160; i < 160; i++)
//         for (int j = -50; j < 50; j++) {
//             ... if (tile.TileType == ModContent.TileType<LumenylCrystals>())
//                 ScreenOverlaysSystem.ThingsToDrawOnTopOfBlur.Add(new DrawData(...));
//         }
//
// 32,000 tiles every frame, unconditionally, wherever the player is. It costs 0.54% to 0.57% of
// the thread in all three of the profiled scenes and found nothing in any of them, because Lumenyl
// Crystals only generate in the Abyss.
//
// The box is also far larger than the screen. It spans 5120 by 1600 pixels against a 1920 by 1080
// view, and the queued DrawData is drawn in world coordinates through Main.GameViewMatrix, so
// everything the sweep finds outside the view is drawn outside the view. Narrowing the sweep to
// the visible tiles plus a tile of margin changes nothing a player can see, and cuts the count by
// roughly three quarters at 1080p and more as the window gets smaller.
//
// The four loop bounds are replaced rather than the body being gated, so the search itself is
// untouched. A crystal on screen is still found, queued and drawn exactly as before. What is
// removed is the part of the sweep that was already drawing off screen.
//
// Read against InfernumMode 2026.6.
public class LumenylCrystalScan : Patch
{
    private const string TargetMod = "InfernumMode";
    private const string TargetType =
        "InfernumMode.Core.GlobalInstances.Systems.TileLightingSystem";

    // The box the mod sweeps, as tile offsets from the player. Matching these pins the patch to
    // the loop it was written against.
    private const int HorizontalReach = 160;
    private const int VerticalReach = 50;

    // One tile of margin on each side, so a crystal straddling the edge of the view is still
    // found. A Lumenyl Crystal occupies a single tile.
    private const int Margin = 1;

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().LumenylCrystalScan;

    public static int HorizontalTiles() => VisibleTiles(Main.screenWidth, HorizontalReach);

    public static int VerticalTiles() => VisibleTiles(Main.screenHeight, VerticalReach);

    public static int NegativeHorizontalTiles() => -HorizontalTiles();

    public static int NegativeVerticalTiles() => -VerticalTiles();

    // Half the visible extent in tiles, rounded up, and never more than the mod's own reach so
    // this can only ever narrow the sweep. Zoom is folded in because Main.screenWidth is in
    // unzoomed pixels while the sweep is measured from the player in world tiles.
    private static int VisibleTiles(int screenPixels, int reach)
    {
        float zoom = Main.GameViewMatrix.Zoom.X;
        int tiles = (int)(screenPixels / (zoom > 0f ? zoom : 1f) / 32f) + 1 + Margin;

        return tiles < reach ? tiles : reach;
    }

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod infernum))
            return;

        MethodInfo updateUi = infernum.Code.GetType(TargetType)?.GetMethod("UpdateUI",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, binder: null,
            [typeof(GameTime)], modifiers: null);

        if (updateUi == null)
        {
            Mod.Logger.Error($"Lumenyl crystal scan: {TargetType}.UpdateUI(GameTime) is missing, " +
                "patch disabled");
            return;
        }

        MonoModHooks.Modify(updateUi, NarrowToScreen);
    }

    // Each bound is matched before anything is written, so a loop that no longer looks like the
    // one this patch read leaves the method untouched rather than half rewritten. Throwing here
    // aborts the whole manipulator, which is what leaves it untouched.
    private void NarrowToScreen(ILContext il)
    {
        List<(Instruction Bound, MethodInfo Accessor)> found = [];
        foreach ((int value, string accessor) in Bounds())
        {
            Instruction bound = Single(il, value);
            if (bound == null)
            {
                Mod.Logger.Error("Lumenyl crystal scan: UpdateUI no longer sweeps a 160 by 50 " +
                    $"tile box, no single load of {value} to replace, patch disabled");
                throw new InvalidOperationException(
                    $"UpdateUI has no single load of {value} to replace");
            }

            found.Add((bound, typeof(LumenylCrystalScan)
                .GetMethod(accessor, BindingFlags.Static | BindingFlags.Public)));
        }

        foreach ((Instruction bound, MethodInfo accessor) in found)
        {
            bound.OpCode = OpCodes.Call;
            bound.Operand = il.Import(accessor);
        }
    }

    // The two loops contribute one load of each bound, so a value appearing anywhere else in the
    // method makes it ambiguous and the patch declines rather than guessing which one it wants.
    private static Instruction Single(ILContext il, int value)
    {
        Instruction match = null;
        foreach (Instruction instruction in il.Body.Instructions)
        {
            if (!instruction.MatchLdcI4(out int loaded) || loaded != value)
                continue;

            if (match != null)
                return null;

            match = instruction;
        }

        return match;
    }

    private static (int Value, string Accessor)[] Bounds() =>
    [
        (-HorizontalReach, nameof(NegativeHorizontalTiles)),
        (HorizontalReach, nameof(HorizontalTiles)),
        (-VerticalReach, nameof(NegativeVerticalTiles)),
        (VerticalReach, nameof(VerticalTiles)),
    ];
}
