using System;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// SeaPrismShaderDrawing hooks Main.DrawTiles and Main.DrawWalls for the whole session and does its
// work in every world, whether or not a sea prism has ever been on screen. The tile hook ends the
// tile and sprite batches, swaps three device textures and their samplers, begins both batches
// again, sweeps every tile of the screen area for one prism type, ends, restores the device and
// begins again -- three times over, once per prism type. The wall hook does the same once for its
// two wall types. DrawTiles alone runs several times a frame, for the solid and non-solid layers
// and again for the render targets, so all of that repeats several times a frame in a world whose
// Sunken Sea nobody is near.
//
// A prism that is not in the screen area cannot be drawn by any of those passes, so one sweep in
// front of the first End decides all of them: find nothing and the hook hands the frame straight
// to orig, leaving the batches and the device exactly as vanilla left them. The sweep walks the
// bounds the mod's own loops walk, guarded by the same WorldGen.InWorld, and it asks Calamity for
// the area rather than recomputing it, so it cannot come to a different answer than the loops it
// stands in for -- at any zoom, and whether the frame is going to the screen or to a render target.
//
// When a prism is on screen the sweep is the only cost added, and it stops at the first one it
// finds. When none is, three screen sweeps and six batch restarts become one sweep.
//
// Read against CalamityMod 2.2.4.
public class SeaPrismPresenceScan : Patch
{
    private const string TargetMod = "CalamityMod";
    private const string TargetType = "CalamityMod.Tiles.SunkenSea.SeaPrismShaderDrawing";

    private delegate void ScreenDrawArea(Vector2 screenPosition, Vector2 offSet,
        out int firstTileX, out int lastTileX, out int firstTileY, out int lastTileY);

    private static ScreenDrawArea _screenDrawArea;
    private static int _prism;
    private static int _crystals;
    private static int _mediumCrystal;
    private static int _wall;
    private static int _unsafeWall;

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().SeaPrismPresenceScan;

    public static bool AnyPrismTilesOnScreen(bool solidLayer)
    {
        // The prism pass is gated on the layer and the target disagreeing and both crystal passes
        // on a non-solid layer, so a solid layer going straight to the screen draws no prism at
        // all and there is nothing for a sweep to find.
        if (solidLayer && Main.drawToScreen)
            return false;

        ScreenArea(out int firstTileX, out int lastTileX, out int firstTileY, out int lastTileY);

        for (int tileY = firstTileY; tileY < lastTileY + 4; tileY++)
        {
            for (int tileX = firstTileX - 2; tileX < lastTileX + 2; tileX++)
            {
                if (!WorldGen.InWorld(tileX, tileY))
                    continue;

                int type = Main.tile[tileX, tileY].TileType;
                if (type == _prism || type == _crystals || type == _mediumCrystal)
                    return true;
            }
        }

        return false;
    }

    public static bool AnyPrismWallsOnScreen()
    {
        ScreenArea(out int firstTileX, out int lastTileX, out int firstTileY, out int lastTileY);

        for (int tileY = firstTileY; tileY < lastTileY + 4; tileY++)
        {
            for (int tileX = firstTileX - 2; tileX < lastTileX + 2; tileX++)
            {
                if (!WorldGen.InWorld(tileX, tileY))
                    continue;

                int type = Main.tile[tileX, tileY].WallType;
                if (type == _wall || type == _unsafeWall)
                    return true;
            }
        }

        return false;
    }

    // The area both hooks draw into: the off-screen border, widened by however far the camera's
    // zoom has pushed the scaled position away from the unscaled one.
    private static void ScreenArea(out int firstTileX, out int lastTileX, out int firstTileY,
        out int lastTileY)
    {
        Vector2 border = Main.drawToScreen ? Vector2.Zero : new Vector2(Main.offScreenRange);
        _screenDrawArea(Main.Camera.UnscaledPosition,
            border + (Main.Camera.UnscaledPosition - Main.Camera.ScaledPosition),
            out firstTileX, out lastTileX, out firstTileY, out lastTileY);
    }

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod calamity))
            return;

        Type owner = calamity.Code.GetType(TargetType);
        MethodInfo tiles = Declared(owner, "DrawSeaPrismsAndCrystals", BindingFlags.Instance);
        MethodInfo walls = Declared(owner, "DrawSeaPrismWalls", BindingFlags.Instance);
        MethodInfo area = Declared(owner, "GetScreenDrawArea", BindingFlags.Static);

        if (tiles == null || walls == null || area == null)
        {
            Mod.Logger.Error($"Sea prism presence scan: {TargetType} has no " +
                "DrawSeaPrismsAndCrystals, DrawSeaPrismWalls or GetScreenDrawArea, patch disabled");
            return;
        }

        // Everything the guards read is resolved before a single method is rewritten, so a
        // Calamity that no longer carries one of these leaves its own drawing untouched.
        _screenDrawArea = area.CreateDelegate<ScreenDrawArea>();
        _prism = calamity.Find<ModTile>("SeaPrism").Type;
        _crystals = calamity.Find<ModTile>("SeaPrismCrystals").Type;
        _mediumCrystal = calamity.Find<ModTile>("MediumSeaPrismCrystal").Type;
        _wall = calamity.Find<ModWall>("SeaPrismWall").Type;
        _unsafeWall = calamity.Find<ModWall>("UnsafeSeaPrismWall").Type;

        MonoModHooks.Modify(tiles,
            il => SkipWithoutPrisms(il, nameof(AnyPrismTilesOnScreen), "solidLayer"));
        MonoModHooks.Modify(walls, il => SkipWithoutPrisms(il, nameof(AnyPrismWallsOnScreen)));
    }

    private static MethodInfo Declared(Type owner, string name, BindingFlags scope) =>
        owner?.GetMethod(name, scope | BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly);

    // `if (!present) { orig(...); return; }` at the top of the hook, ahead of the shader setup and
    // of every batch it ends. The guard is handed the hook arguments named in `arguments`, and the
    // jump lands on the tail call to orig the hook already carries rather than repeating it, so the
    // vanilla draw runs exactly once either way.
    private void SkipWithoutPrisms(ILContext il, string predicate, params string[] arguments)
    {
        ParameterDefinition[] passed = new ParameterDefinition[arguments.Length];
        for (int index = 0; index < arguments.Length; index++)
        {
            passed[index] = il.Method.Parameters
                .FirstOrDefault(parameter => parameter.Name == arguments[index]);

            if (passed[index] == null)
            {
                Mod.Logger.Error($"Sea prism presence scan: {il.Method.Name} has no " +
                    $"{arguments[index]} argument to hand the guard, patch disabled");
                return;
            }
        }

        ILCursor cursor = new ILCursor(il);

        // That tail hands orig every argument the hook was given, in order and one ldarg each.
        Func<Instruction, bool>[] tail =
            new Func<Instruction, bool>[il.Method.Parameters.Count + 1];
        for (int parameter = 0; parameter < il.Method.Parameters.Count; parameter++)
        {
            int argument = parameter + 1;
            tail[parameter] = instruction => instruction.MatchLdarg(argument);
        }

        tail[^1] = instruction => instruction.MatchCallvirt(out MethodReference called)
            && called.Name == "Invoke";

        if (!cursor.TryGotoNext(MoveType.Before, tail))
        {
            Mod.Logger.Error($"Sea prism presence scan: {il.Method.Name} no longer ends by " +
                "passing its own arguments on to orig, patch disabled");
            return;
        }

        ILLabel callOriginal = cursor.MarkLabel();
        cursor.Goto(0);
        foreach (ParameterDefinition parameter in passed)
            cursor.Emit(OpCodes.Ldarg, parameter);

        cursor.Emit(OpCodes.Call, typeof(SeaPrismPresenceScan).GetMethod(predicate));
        cursor.Emit(OpCodes.Brfalse, callOriginal);
    }
}
