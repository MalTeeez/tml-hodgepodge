using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// InfernalGlobalNPC.PreAI runs for every NPC every tick, and ends with a Thorium block shaped like
// this:
//
//     if (InfernalCrossmod.Thorium.Loaded) {
//         for (int j = 0; j < 255; j++) {
//             Player player = Main.player[j];
//             if (!player.dead && player.active && npc.WithinRange(player.Center, 10000f)) {
//                 if (npc.ModNPC?.Mod.Name != "ThoriumMod" && npc.boss)
//                     player.ClearBuff(InfernalCrossmod.Thorium.Mod.Find<ModBuff>("SpiritualistBuff").Type);
//             }
//         }
//     }
//
// The only thing the loop can do requires npc.boss, and npc.boss is tested last. So every ordinary
// enemy in the world pays a 255 slot walk, with a dead test, an active test and a distance check
// per slot, to reach a branch it can never enter. In the blood moon capture that is 1.34% of the
// thread in this method's own body, against 0.33% while idle, because the cost scales with how
// many NPCs are alive rather than with how many are bosses.
//
// The guard is hoisted to the front of the block. Nothing in the loop writes npc.boss, so asking
// once before it is the same question the loop asks on every iteration. A boss still clears the
// buff on every player in range exactly as before.
//
// The patch borrows the branch the Loaded test already uses to skip the block, so it cannot invent
// a jump target of its own. Both anchors have to match or nothing is written.
//
// Read against InfernalEclipseAPI 0.10.8.
public class InfernalBossBuffScan : Patch
{
    private const string TargetMod = "InfernalEclipseAPI";
    private const string TargetType = "InfernalEclipseAPI.Common.GlobalNPCs.InfernalGlobalNPC";
    private const string CrossmodType = "InfernalEclipseAPI.Core.Systems.InfernalCrossmod/Thorium";

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().InfernalBossBuffScan;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod infernalEclipse))
            return;

        MethodInfo preAi = infernalEclipse.Code.GetType(TargetType)?.GetMethod("PreAI",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, binder: null,
            [typeof(NPC)], modifiers: null);

        if (preAi == null)
        {
            Mod.Logger.Error($"Infernal boss buff scan: {TargetType}.PreAI(NPC) is missing, " +
                "patch disabled");
            return;
        }

        MonoModHooks.Modify(preAi, HoistBossTest);
    }

    private void HoistBossTest(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);

        // `if (InfernalCrossmod.Thorium.Loaded)` opening the block, and the branch it takes to
        // skip it. That branch is where a non-boss should go too.
        ILLabel afterBlock = null;
        if (!cursor.TryGotoNext(MoveType.After,
                i => i.MatchCall(CrossmodType, "get_Loaded"),
                i => i.MatchBrfalse(out afterBlock)))
        {
            Mod.Logger.Error("Infernal boss buff scan: PreAI no longer opens its Thorium block " +
                "with a Loaded test, patch disabled");
            return;
        }

        // The block has to be the last thing in the method for its skip branch to be a safe
        // destination. It is: PreAI ends by calling base and returning, and the Loaded test is the
        // only use of this label. A release that puts more work after the loop would land here.
        if (!EndsMethod(afterBlock.Target))
        {
            Mod.Logger.Error("Infernal boss buff scan: the Thorium block is no longer the last " +
                "thing PreAI does, so skipping it would skip other work, patch disabled");
            return;
        }

        cursor.Emit(OpCodes.Ldarg_1);
        cursor.Emit(OpCodes.Ldfld, typeof(NPC).GetField(nameof(NPC.boss)));
        cursor.Emit(OpCodes.Brfalse, afterBlock);
    }

    // `return base.PreAI(npc);` -- the instance, the NPC, the call, the return. Anything else
    // after the block means the label is not a safe place to send a non-boss.
    private static bool EndsMethod(Instruction target)
    {
        Instruction argument = target?.Next;
        Instruction call = argument?.Next;
        Instruction ret = call?.Next;

        return target.MatchLdarg(0) && argument.MatchLdarg(1)
            && call?.OpCode == OpCodes.Call && ret?.OpCode == OpCodes.Ret;
    }
}
