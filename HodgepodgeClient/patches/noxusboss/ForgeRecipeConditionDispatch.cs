using System;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// NoxusBoss's Starlit Forge grants every recipe all of its environment and tile conditions while
// the player stands next to one, and implements that with two detours on Recipe whose bodies are
// nothing but:
//
//     if (player.adjTile[Type])
//         return true;
//     return orig.Invoke(player, tempRec);
//
// Recipe.FindRecipes tests both per recipe, and FindRecipes reruns on every inventory change --
// continuously while mining a vein. At 0.130 ms/tick these two detours cost more than NoxusBoss's
// other 73 tile disabling hooks put together, and all of it is detour dispatch wrapped around one
// array read.
//
// Inline the array read into the two Recipe methods and drop the detours, leaving one test where
// there was a test plus two trampolines.
public class ForgeRecipeConditionDispatch : Patch
{
    private const string TargetMod = "NoxusBoss";
    private const string ForgeTileName = "StarlitForgeTile";

    // Both detours byte for byte, with the three metadata tokens left open. Replicating a method
    // means owning it, so the whole body is pinned rather than its length: a same-length rewrite
    // -- a different tile, an extra condition, a changed return -- has to fail this check, or the
    // patch drops NoxusBoss's real hooks and substitutes behaviour that no longer matches.
    private static readonly byte?[] ExpectedDetourBody =
    [
        0x04,                           // ldarg.2  (player)
        0x6F, null, null, null, null,   // callvirt Player.get_adjTile
        0x02,                           // ldarg.0  (this)
        0x28, null, null, null, null,   // call ModBlockType.get_Type
        0x91,                           // ldelem.u1
        0x2B, 0x02,                     // brfalse.s over the early return
        0x17,                           // ldc.i4.1
        0x2A,                           // ret
        0x03, 0x04, 0x05,               // ldarg.1 .. ldarg.3
        0x6F, null, null, null, null,   // callvirt orig.Invoke
        0x2A                            // ret
    ];

    private static ushort _forgeType;
    private static int _injected;

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().ForgeRecipeConditionDispatch;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out _)
            || !ModContent.TryFind(TargetMod, ForgeTileName, out ModTile forge))
            return;

        // Static, so a reload would otherwise carry the previous load's count into the check below.
        _injected = 0;

        MethodInfo environmentDetour = FindDetour(forge.GetType(),
            "EnableAllEnvironmentConditionsForForge");
        MethodInfo tileDetour = FindDetour(forge.GetType(), "EnableAllTileConditionsForForge");

        if (environmentDetour == null || tileDetour == null)
        {
            Mod.Logger.Error($"Forge recipe condition dispatch: {ForgeTileName} no longer declares " +
                "both condition detours, patch disabled");
            return;
        }

        if (!IsPlainAdjacencyCheck(environmentDetour) || !IsPlainAdjacencyCheck(tileDetour))
        {
            Mod.Logger.Error("Forge recipe condition dispatch: a forge condition detour is no " +
                "longer a bare adjacency check, patch disabled so NoxusBoss keeps its own hooks");
            return;
        }

        _forgeType = forge.Type;
        MethodInfo environmentCondition =
            RecipeCondition(nameof(On_Recipe.PlayerMeetsEnvironmentConditions));
        MethodInfo tileCondition = RecipeCondition(nameof(On_Recipe.PlayerMeetsTileRequirements));
        MonoModHooks.Modify(environmentCondition, GrantForgeConditions);
        MonoModHooks.Modify(tileCondition, GrantForgeConditions);

        // Only now is the check running in both places at once, which is the safe state to remove
        // one of them from. Reversing this order deletes the forge's feature if an inject missed.
        if (_injected != 2)
        {
            Mod.Logger.Error("Forge recipe condition dispatch: the adjacency test did not inject " +
                "into both Recipe methods, NoxusBoss's detours left in place");
            return;
        }

        Detach<On_Recipe.hook_PlayerMeetsEnvironmentConditions>(environmentCondition, forge,
            environmentDetour, handler => On_Recipe.PlayerMeetsEnvironmentConditions -= handler);
        Detach<On_Recipe.hook_PlayerMeetsTileRequirements>(tileCondition, forge,
            tileDetour, handler => On_Recipe.PlayerMeetsTileRequirements -= handler);
    }

    private static MethodInfo FindDetour(Type forgeTile, string name) =>
        forgeTile.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly);

    private static MethodInfo RecipeCondition(string name) =>
        typeof(Recipe).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

    // Vanilla passes the player as the first argument, so the inlined test reads the same array
    // slot the detour did, one call earlier and without the trampoline.
    private void GrantForgeConditions(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);
        ILLabel checkNormally = cursor.DefineLabel();
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.EmitDelegate<Func<Player, bool>>(StandingByForge);
        cursor.Emit(OpCodes.Brfalse, checkNormally);
        cursor.Emit(OpCodes.Ldc_I4_1);
        cursor.Emit(OpCodes.Ret);
        cursor.MarkLabel(checkNormally);
        _injected++;
    }

    private static bool StandingByForge(Player player) => player.adjTile[_forgeType];

    // Opcodes have to match exactly and the three operands have to name the members being
    // replicated; a token pointing anywhere else means this is a different method that happens to
    // be the same shape.
    private static bool IsPlainAdjacencyCheck(MethodInfo detour)
    {
        byte[] body = detour.GetMethodBody()?.GetILAsByteArray();
        if (body == null || body.Length != ExpectedDetourBody.Length)
            return false;

        for (int offset = 0; offset < body.Length; offset++)
        {
            if (ExpectedDetourBody[offset] is byte expected && body[offset] != expected)
                return false;
        }

        Module module = detour.Module;
        return module.ResolveMethod(BitConverter.ToInt32(body, 2)) is { Name: "get_adjTile" }
            && module.ResolveMethod(BitConverter.ToInt32(body, 8)) is { Name: "get_Type" }
            && module.ResolveMethod(BitConverter.ToInt32(body, 21)) is { Name: "Invoke" };
    }
}
