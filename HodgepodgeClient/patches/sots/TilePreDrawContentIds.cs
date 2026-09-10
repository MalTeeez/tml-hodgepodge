using System;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework.Graphics;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// SOTSTile.PreDraw is a GlobalTile hook, so it runs for every tile drawn, every frame. It opens
// with a chain of comparisons shaped like this, one pair per dissolving tile:
//
//     if (tile.WallType == ModContent.WallType<NatureWallWall>()
//         && tile.TileType != ModContent.TileType<DissolvingNatureTile>())
//
// -- sixteen or more ModContent lookups per tile, for numbers that are handed out once when
// content is registered and never change afterwards. Measured at 0.032 ms/frame just resolving
// them, inside a method costing 0.068 ms/frame of its own work.
//
// Every one of those calls is replaced with the id it returns, resolved once here. That is what
// the mod would have compiled to if the ids were constants, and it is safe for the same reason:
// this runs in PostSetupContent, by which point every id is assigned, and it runs again on a
// reload, when they may be different.
public class TilePreDrawContentIds : Patch
{
    private const string TargetMod = "SOTS";
    private const string TargetType = "SOTS.SOTSTile";

    // Both are `static int Name<T>()` on ModContent and both answer with a registered content id.
    private static readonly string[] Lookups = ["TileType", "WallType"];

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().TilePreDrawContentIds;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod sots))
            return;

        MethodInfo preDraw = sots.Code.GetType(TargetType)?.GetMethod("PreDraw",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, binder: null,
            [typeof(int), typeof(int), typeof(int), typeof(SpriteBatch)], modifiers: null);

        if (preDraw == null)
        {
            Mod.Logger.Error($"Tile pre draw content ids: {TargetType}.PreDraw is missing, " +
                "patch disabled");
            return;
        }

        MonoModHooks.Modify(preDraw, FoldContentIds);
    }

    private void FoldContentIds(ILContext il)
    {
        int folded = 0;
        foreach (Instruction instruction in il.Body.Instructions)
        {
            if (instruction.OpCode != OpCodes.Call
                || instruction.Operand is not GenericInstanceMethod lookup
                || !Lookups.Contains(lookup.ElementMethod.Name)
                || lookup.ElementMethod.DeclaringType.Name != nameof(ModContent))
                continue;

            int? id = Resolve(lookup);
            if (id == null)
                continue;

            instruction.OpCode = OpCodes.Ldc_I4;
            instruction.Operand = id.Value;
            folded++;
        }

        // Nothing folded means the comparisons are no longer written as ModContent lookups, so the
        // method is doing something this patch has not read. It is left exactly as it was.
        if (folded == 0)
            Mod.Logger.Error("Tile pre draw content ids: PreDraw no longer resolves any content " +
                "ids, left as it is");
        else
            Mod.Logger.Info($"Tile pre draw content ids: folded {folded} lookups into constants");
    }

    // Calls the lookup once, now, for the value it will return every time from here on. Cecil
    // spells a nested type with a slash where reflection wants a plus.
    private int? Resolve(GenericInstanceMethod lookup)
    {
        try
        {
            Type content = Type.GetType(lookup.GenericArguments[0].FullName.Replace('/', '+')
                + ", " + lookup.GenericArguments[0].Resolve().Module.Assembly.Name.Name);
            MethodInfo generic = typeof(ModContent)
                .GetMethod(lookup.ElementMethod.Name, BindingFlags.Static | BindingFlags.Public);

            if (content == null || generic == null)
                return null;

            return (int)generic.MakeGenericMethod(content).Invoke(null, null);
        }
        catch (Exception exception)
        {
            Mod.Logger.Error($"Tile pre draw content ids: {lookup.GenericArguments[0].Name} did " +
                $"not resolve, left as a call, {exception.Message}");
            return null;
        }
    }
}
