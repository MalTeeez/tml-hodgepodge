using System;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
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

    // Pin the complete meaning of the detour rather than its serialized bytes. The previous byte
    // guard encoded brfalse.s as br.s and therefore rejected the unchanged NoxusBoss 1.2 body;
    // semantic matching also avoids making metadata tokens and branch width part of the contract.
    private static bool IsPlainAdjacencyCheck(MethodInfo detour)
    {
        Instruction[] body = InstructionsOf(detour)
            .Where(instruction => instruction.OpCode != OpCodes.Nop).ToArray();
        if (body.Length != 13)
            return false;

        return body[0].MatchLdarg(2)
            && IsCall(body[1], "get_adjTile", typeof(Player).FullName)
            && body[2].MatchLdarg(0)
            && IsCall(body[3], "get_Type", typeof(ModBlockType).FullName)
            && body[4].OpCode == OpCodes.Ldelem_U1
            && (body[5].OpCode == OpCodes.Brfalse || body[5].OpCode == OpCodes.Brfalse_S)
            && SkipNops(body[5].Operand as Instruction) == body[8]
            && body[6].MatchLdcI4(1)
            && body[7].OpCode == OpCodes.Ret
            && body[8].MatchLdarg(1)
            && body[9].MatchLdarg(2)
            && body[10].MatchLdarg(3)
            && IsCall(body[11], "Invoke", declaringType: null)
            && body[12].OpCode == OpCodes.Ret;
    }

    private static bool IsCall(Instruction instruction, string name, string declaringType) =>
        (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
        && instruction.Operand is MethodReference called && called.Name == name
        && (declaringType == null || called.DeclaringType.FullName == declaringType);

    private static Instruction SkipNops(Instruction instruction)
    {
        while (instruction?.OpCode == OpCodes.Nop)
            instruction = instruction.Next;

        return instruction;
    }
}
