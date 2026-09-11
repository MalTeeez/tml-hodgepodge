using System;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// StarsAbovePlayer.StellarNova runs from PreUpdate, once per player per tick. Half of it is
// gameplay: it sums the equipped prisms into the nova stat modifiers, applies set bonuses -- which
// also feed the nova gauge and clamp the exalt stacks, so that half has to run every tick -- and
// picks the nova's base numbers. The other half builds seven strings for the Stellar Nova panel:
//
//     setBonusInfo += $"{value}{value2}:{LangHelper.GetTextValue("StellarNova.StellarPrisms." + name + ".Name")} ({item.Value}/{num2})]\n";
//     modStats = $"\n{Math.Round((double)novaDamage * (1.0 + novaDamageMod / 100.0), 0)} " + LangHelper.GetTextValue("StellarNova.StellarNovaInfo.Damage") + ...
//     abilityDescription = LangHelper.Wrap(abilityDescription, 85);
//
// a dozen localisation lookups, an Enum.GetName, twenty-odd interpolations and two word wraps,
// per player per tick, for a panel that is closed. 0.72% of the lower-end client's thread, of
// which the strings were three quarters.
//
// The strings have exactly one reader, StellarNovaUI, which copies them into its text elements on
// every update while it is open and draws only at novaUIOpacity of 0.1 or more. The opacity only
// climbs in PreUpdate, after this method has run with novaUIActive already set, so a frame the
// panel can be seen on is a frame whose tick rebuilt the strings. That is the gate: the two
// string-building stretches are skipped unless the panel is active or still fading out.
//
// The stretches are found by their anchors, and each is checked to store nothing but the seven
// strings before it is gated. The gameplay stores between and around them are untouched.
//
// Read against StarsAbove 2.1.8.4.
public class StellarNovaTextGate : Patch
{
    private const string TargetMod = "StarsAbove";
    private const string TargetType = "StarsAbove.StarsAbovePlayer";

    private static readonly string[] TextFields =
    [
        "setBonusInfo", "abilityName", "abilitySubName", "abilityDescription", "starfarerBonus",
        "baseStats", "modStats",
    ];

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().StellarNovaTextGate;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod starsAbove))
            return;

        Type owner = starsAbove.Code.GetType(TargetType);
        MethodInfo stellarNova = owner?.GetMethod("StellarNova",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            binder: null, Type.EmptyTypes, modifiers: null);
        FieldInfo active = owner?.GetField("novaUIActive", BindingFlags.Instance | BindingFlags.Public);
        FieldInfo opacity = owner?.GetField("novaUIOpacity", BindingFlags.Instance | BindingFlags.Public);

        if (stellarNova?.ReturnType != typeof(void) || active?.FieldType != typeof(bool)
            || opacity?.FieldType != typeof(float))
        {
            Mod.Logger.Error($"Stellar nova text gate: {TargetType}.StellarNova(), a bool " +
                "novaUIActive or a float novaUIOpacity is missing, patch disabled");
            return;
        }

        MonoModHooks.Modify(stellarNova, il => GateTextBlocks(il, active, opacity));
    }

    private void GateTextBlocks(ILContext il, FieldInfo active, FieldInfo opacity)
    {
        // The set bonus text: from the dictionary load that feeds the LINQ Count up to the
        // NovaSetBonuses call that consumes the same dictionary for gameplay.
        ILCursor loop = new ILCursor(il);
        ILCursor loopEnd = new ILCursor(il);
        if (!loop.TryGotoNext(MoveType.Before, IsCallTo("Count", nameof(Enumerable)))
            || !loop.TryGotoPrev(MoveType.Before, instruction => instruction.MatchLdloc(out int local)
                && il.Body.Variables[local].VariableType.Name.StartsWith("Dictionary"))
            || !loopEnd.TryGotoNext(MoveType.Before, IsCallTo("NovaSetBonuses", "StarsAbovePlayer"))
            || !loopEnd.TryGotoPrev(MoveType.Before, instruction => instruction.MatchLdarg(0)))
        {
            Mod.Logger.Error("Stellar nova text gate: the set bonus text loop is no longer " +
                "between the dictionary count and NovaSetBonuses, patch disabled");
            return;
        }

        // The nova text: everything after the last of the base number stores, through the two
        // word wraps, to the return.
        ILCursor tail = new ILCursor(il);
        if (!tail.TryGotoNext(MoveType.After, instruction => instruction.MatchStfld(out FieldReference stored)
                && stored.Name == "novaEffectDuration"))
        {
            Mod.Logger.Error("Stellar nova text gate: StellarNova no longer stores " +
                "novaEffectDuration before building the nova text, patch disabled");
            return;
        }

        if (!OnlyStoresText(il, loop.Next, loopEnd.Next) || !OnlyStoresText(il, tail.Next, null))
        {
            Mod.Logger.Error("Stellar nova text gate: a gated stretch of StellarNova now stores " +
                "something besides the panel's strings, patch disabled");
            return;
        }

        ILLabel afterLoop = loopEnd.MarkLabel();
        EmitGate(tail, active, opacity, skipTo: null);
        loop.MoveAfterLabels();
        EmitGate(loop, active, opacity, afterLoop);
    }

    // `if (!novaUIActive && !(novaUIOpacity > 0f)) goto skipTo;` where a null skipTo returns.
    // The float test is unordered, so a NaN opacity takes the original path.
    private static void EmitGate(ILCursor cursor, FieldInfo active, FieldInfo opacity,
        ILLabel skipTo)
    {
        ILLabel build = cursor.DefineLabel();
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Ldfld, active);
        cursor.Emit(OpCodes.Brtrue, build);
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Ldfld, opacity);
        cursor.Emit(OpCodes.Ldc_R4, 0f);
        cursor.Emit(OpCodes.Bgt_Un, build);
        if (skipTo == null)
            cursor.Emit(OpCodes.Ret);
        else
            cursor.Emit(OpCodes.Br, skipTo);
        cursor.MarkLabel(build);
    }

    // Every field store from `first` up to `end` (or the end of the method) targets one of the
    // seven strings. Locals are free to change: nothing after a gated stretch reads them. The
    // compiler's own lambda cache is a static it may store on the way.
    private static bool OnlyStoresText(ILContext il, Instruction first, Instruction end)
    {
        for (Instruction instruction = first; instruction != null && instruction != end;
            instruction = instruction.Next)
        {
            if (instruction.OpCode != OpCodes.Stfld && instruction.OpCode != OpCodes.Stsfld)
                continue;

            string field = ((FieldReference)instruction.Operand).Name;
            if (!field.StartsWith("<>") && !TextFields.Contains(field))
                return false;
        }

        return true;
    }

    private static Func<Instruction, bool> IsCallTo(string name, string owner) => instruction =>
        instruction.MatchCall(out MethodReference called)
        && called.Name == name && called.DeclaringType.Name == owner;
}
