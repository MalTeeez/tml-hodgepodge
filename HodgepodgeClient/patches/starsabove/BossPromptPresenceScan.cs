using System;
using System.Collections.Generic;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// StarsAbovePlayer runs two boss prompt methods per player per tick. Between them they hold about
// ninety copies of one block, each for a boss the Starfarer has a line about:
//
//     if (result.TryFind<ModNPC>("Crabulon", out var value) && NPC.AnyNPCs(value.Type) && !seenCrabulon)
//     {
//         ...
//         starfarerPromptActive("onCrabulon");
//     }
//
// That is a name lookup and a full 200 slot world scan per boss per player per tick, in a world
// where nearly every prompt has already played. Together the two were 0.57% of the lower-end
// client's thread in every scene, boss fight or not.
//
// Every block needs its boss alive before anything inside it can run, so a tick with none of those
// bosses alive is a tick where both methods do nothing. The ids are read out of the methods' own
// IL at load -- the constant in front of each AnyNPCs, the names handed to TryFind -- and one pass
// over the active NPCs per tick decides whether to run the originals. When a listed boss is alive
// they run untouched, prompts and seen flags included.
//
// The named lookups resolve every name against every mod the method asks for, rather than pairing
// each name with its own TryGetMod. That can only widen the set of NPCs that let the originals run,
// never narrow it, and the originals decide the rest. An AnyNPCs whose argument is neither a
// constant nor a Type read off a found ModNPC is a shape this patch has not read, and it leaves
// that method alone.
//
// Read against StarsAbove 2.1.8.4.
public class BossPromptPresenceScan : Patch
{
    private const string TargetMod = "StarsAbove";
    private const string TargetType = "StarsAbove.StarsAbovePlayer";

    private static readonly string[] PromptMethods =
        ["BossStarfarerPrompts", "OtherModBossStarfarerPrompts"];

    private static bool[] _watched;
    private static uint _scannedTick = uint.MaxValue;
    private static bool _anyAlive;

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().BossPromptPresenceScan;

    public static bool AnyWatchedBossAlive()
    {
        if (_scannedTick == Main.GameUpdateCount)
            return _anyAlive;

        _anyAlive = false;
        foreach (NPC npc in Main.ActiveNPCs)
        {
            if (_watched[npc.type])
            {
                _anyAlive = true;
                break;
            }
        }

        _scannedTick = Main.GameUpdateCount;
        return _anyAlive;
    }

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod starsAbove))
            return;

        Type owner = starsAbove.Code.GetType(TargetType);
        MethodInfo[] prompts = new MethodInfo[PromptMethods.Length];
        for (int method = 0; method < prompts.Length; method++)
        {
            prompts[method] = owner?.GetMethod(PromptMethods[method],
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                binder: null, Type.EmptyTypes, modifiers: null);

            if (prompts[method]?.ReturnType != typeof(void))
            {
                Mod.Logger.Error($"Boss prompt presence scan: {TargetType}.{PromptMethods[method]}" +
                    "() is missing, patch disabled");
                return;
            }
        }

        _watched = new bool[NPCLoader.NPCCount];
        for (int method = 0; method < prompts.Length; method++)
        {
            string name = PromptMethods[method];
            MonoModHooks.Modify(prompts[method], il => GateOnPresence(il, name, starsAbove));
        }
    }

    private void GateOnPresence(ILContext il, string method, Mod starsAbove)
    {
        // StarsAbove's own bosses are asked for through ModContent.NPCType<T>(); folded, they are
        // constants like the vanilla ones and read the same way below.
        List<string> skipped = [];
        ContentIdFolding.FoldContentLookups(il, ContentIdFolding.InAssembly(starsAbove.Code),
            skipped.Add);
        foreach (string reason in skipped)
            Mod.Logger.Error($"Boss prompt presence scan: {reason}, left as a call");

        if (!CollectWatchedBosses(il, out string unreadable))
        {
            Mod.Logger.Error($"Boss prompt presence scan: {method} {unreadable}, left as it is");
            return;
        }

        ILCursor cursor = new ILCursor(il);
        ILLabel run = cursor.DefineLabel();
        cursor.Emit(OpCodes.Call,
            typeof(BossPromptPresenceScan).GetMethod(nameof(AnyWatchedBossAlive)));
        cursor.Emit(OpCodes.Brtrue, run);
        cursor.Emit(OpCodes.Ret);
        cursor.MarkLabel(run);
    }

    // Marks every NPC the method can ask about. False, with the reason, when any AnyNPCs call
    // takes an argument this cannot account for.
    private static bool CollectWatchedBosses(ILContext il, out string unreadable)
    {
        HashSet<string> mods = [];
        HashSet<string> names = [];
        int constants = 0;
        int typeReads = 0;
        unreadable = null;

        foreach (Instruction instruction in il.Body.Instructions)
        {
            if (IsStaticCall(instruction, nameof(ModLoader.TryGetMod), nameof(ModLoader))
                && PrecedingString(instruction) is { } modName)
                mods.Add(modName);
            else if (IsNamedFind(instruction) && PrecedingString(instruction) is { } npcName)
                names.Add(npcName);
            else if (IsStaticCall(instruction, nameof(NPC.AnyNPCs), nameof(NPC)))
            {
                if (instruction.Previous.MatchLdcI4(out int id))
                {
                    Watch(id);
                    constants++;
                }
                else if (IsTypeGetter(instruction.Previous))
                    typeReads++;
                else
                {
                    unreadable = "asks about an NPC in a shape this patch does not read";
                    return false;
                }
            }
        }

        if (constants + typeReads == 0 || (typeReads > 0 && names.Count == 0))
        {
            unreadable = "no longer asks about NPCs the way this patch was written against";
            return false;
        }

        foreach (string modName in mods)
        {
            if (!ModLoader.TryGetMod(modName, out Mod owner))
                continue;

            foreach (string npcName in names)
            {
                if (owner.TryFind(npcName, out ModNPC boss))
                    Watch(boss.Type);
            }
        }

        return true;
    }

    private static void Watch(int type)
    {
        if (type >= 0 && type < _watched.Length)
            _watched[type] = true;
    }

    // The string an out-argument call was given: `ldstr "Crabulon"; ldloca value; callvirt`.
    private static string PrecedingString(Instruction call)
    {
        Instruction previous = call.Previous;
        for (int back = 0; back < 2 && previous != null; back++, previous = previous.Previous)
        {
            if (previous.OpCode == OpCodes.Ldstr)
                return (string)previous.Operand;
        }

        return null;
    }

    private static bool IsStaticCall(Instruction instruction, string name, string owner) =>
        instruction.MatchCall(out MethodReference called)
        && called.Name == name && called.DeclaringType.Name == owner;

    private static bool IsNamedFind(Instruction instruction) =>
        instruction.MatchCallvirt(out MethodReference called)
        && called.Name == nameof(Mod.TryFind) && called.DeclaringType.Name == nameof(Mod);

    private static bool IsTypeGetter(Instruction instruction) =>
        instruction.MatchCallvirt(out MethodReference getter)
        && getter.Name == "get_Type" && getter.Parameters.Count == 0;
}
