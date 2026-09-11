using System;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeServer;

// DebuffNPC.PostAI walks every one of the 1001 slots in Main.projectile, unconditionally, once for
// every NPC in the world every tick. Each slot is put through seven separate condition chains
// looking for the projectile types SOTS attaches effects to.
//
// The work scales with NPC count, which is what makes it a boss fight problem rather than a
// background one. On the profiled server it was 0.290 ms of a 9.02 ms tick, and on a client
// capture it was 4710 ms of 240 seconds, 1.96% of the thread. In both cases 96% of that is the
// method's own inlined body rather than anything it calls, which is what a loop of field loads
// and branches looks like to a sampling profiler.
//
// Every one of the seven chains already requires projectile.active, so a slot holding a dead
// projectile cannot reach any of them. Skipping such a slot up front is therefore exactly
// equivalent, and it cuts the cost of a dead slot from eight or so field loads and branches to
// one load and one branch. The live slots are untouched.
//
// The guard is inserted at the top of the loop body and jumps to the increment, borrowing the
// label the method's own `continue` already targets. Both anchors have to match or nothing is
// written, which also means a SOTS release that iterates Main.ActiveProjectiles instead turns
// this patch off by itself rather than fighting it.
public class DebuffProjectileScan : Patch
{
    private const string TargetMod = "SOTS";
    private const string TargetType = "SOTS.Common.GlobalNPCs.DebuffNPC";
    private const string LoopEndField = "AccretionSingularity";

    protected override bool Enabled => ModContent.GetInstance<ServerConfig>().DebuffProjectileScan;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod sots))
            return;

        MethodInfo postAi = sots.Code.GetType(TargetType)?.GetMethod("PostAI",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
            binder: null, [typeof(NPC)], modifiers: null);

        if (postAi == null)
        {
            Mod.Logger.Error($"Debuff projectile scan: {TargetType}.PostAI(NPC) is missing, " +
                "patch disabled");
            return;
        }

        MonoModHooks.Modify(postAi, SkipInactiveSlots);
    }

    private void SkipInactiveSlots(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);

        // The last of the seven chains is a plain `continue`, so its branch already points at the
        // loop increment. Taking the label from there beats trying to recognise the increment.
        ILLabel nextSlot = null;
        if (!cursor.TryGotoNext(MoveType.After,
                i => i.MatchLdsfld(TargetType, LoopEndField),
                i => i.MatchBneUn(out nextSlot)))
        {
            Mod.Logger.Error($"Debuff projectile scan: no {LoopEndField} test to take the loop " +
                "increment from, patch disabled");
            return;
        }

        // And it has to be the increment rather than some other branch target. Without this a
        // reordering of the seven chains would leave the guard jumping into the middle of the
        // loop body, which fails silently instead of not applying.
        if (!IsCounterIncrement(nextSlot.Target))
        {
            Mod.Logger.Error($"Debuff projectile scan: the {LoopEndField} test no longer branches " +
                "to the loop increment, patch disabled");
            return;
        }

        // `Projectile projectile = Main.projectile[n];` opening the loop body. The other reads of
        // Main.projectile in this method index it with an expression, so none of them match four
        // instructions in a row.
        cursor.Index = 0;
        int projectile = -1;
        if (!cursor.TryGotoNext(MoveType.After,
                i => i.MatchLdsfld(typeof(Main), nameof(Main.projectile)),
                i => i.MatchLdloc(out _),
                i => i.MatchLdelemRef(),
                i => i.MatchStloc(out projectile)))
        {
            Mod.Logger.Error("Debuff projectile scan: PostAI no longer opens its loop body by " +
                "storing Main.projectile[n], patch disabled");
            return;
        }

        cursor.Emit(OpCodes.Ldloc, il.Body.Variables[projectile]);
        cursor.Emit(OpCodes.Ldfld, typeof(Entity).GetField(nameof(Entity.active)));
        cursor.Emit(OpCodes.Brfalse, nextSlot);
    }

    // `n = n + 1` over one local, which is the shape of the increment of a C# for loop and the
    // only place in this method a `continue` can legitimately land.
    private static bool IsCounterIncrement(Instruction target)
    {
        Instruction one = target?.Next;
        Instruction add = one?.Next;
        Instruction store = add?.Next;

        return target.MatchLdloc(out int counter) && one.MatchLdcI4(1)
            && add.OpCode == OpCodes.Add && store != null && store.MatchStloc(counter);
    }
}
