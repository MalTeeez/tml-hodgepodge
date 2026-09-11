using System;
using System.Reflection;
using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// The Void Anomaly is a persistent town projectile, so its EntityCollision runs every tick for as
// long as the structure exists. It walks 400 item slots, and inside the same loop 200 NPC slots
// and 255 player slots, handing every active one to AcceptEntity or RejectEntity:
//
//     for (int i = 0; i < Main.maxItems; i++) {
//         Item item = Main.item[i];
//         if (item.active && !item.GetGlobalItem<GlobalEntityItem>().RecentlyTeleported && !VoidAnomalyIsShattered) {
//             if (num == -1f) AcceptEntity(item, i); else RejectEntity(item, num);
//         }
//         ...
//
// It costs 0.71% to 0.98% of the thread in all three profiled scenes, which is what a cost that
// never stops looks like: it does not care whether a boss is alive or whether anything is near the
// anomaly at all.
//
// Both helpers already do nothing beyond a range, and both establish that range only after a
// length, a normalise and some vector work per entity. AcceptEntity has an outer bound of 640
// units, past which every branch in it is closed. RejectEntity acts when the entity is inside the
// barrier, or when the entity's hitbox covers the point on the barrier in its direction, and that
// second case needs the entity within the barrier plus half its own diagonal.
//
// So each gets a squared distance test in front, generous enough that it can only skip an entity
// both helpers would have left alone. Half the diagonal is bounded by width plus height, which is
// used instead because it is two field loads rather than a square root.
//
// The bounds are read out of the methods rather than trusted from this comment. Neither guard is
// written unless the constant it rests on is still there, so a SOTS release that changes a reach
// turns its own guard off instead of culling at the wrong distance.
//
// Read against SOTS 0.25.1.9.
public class VoidAnomalyRangeGuard : Patch
{
    private const string TargetMod = "SOTS";
    private const string TargetType = "SOTS.NPCs.Town.VoidAnomaly";

    // AcceptEntity's outer bound. Past this its pull, its teleport and its portal counter are all
    // unreachable, so the whole method is a no-op.
    private const float AcceptReach = 640f;

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().VoidAnomalyRangeGuard;

    // True when AcceptEntity cannot do anything for this entity, so the call can be skipped.
    public static bool OutsideAcceptReach(Entity anomaly, Entity entity) =>
        Vector2.DistanceSquared(anomaly.Center, entity.Center) > AcceptReach * AcceptReach;

    // True when RejectEntity cannot do anything for this entity. The barrier point it tests
    // against sits exactly barrierSize from the anomaly, so an entity whose centre is further away
    // than the barrier plus its own extent cannot have a hitbox reaching that point either.
    public static bool OutsideRejectReach(Entity anomaly, Entity entity, float barrierSize)
    {
        // Absolute, because a barrier width the mod computes as negative would put its test point
        // on the far side of the anomaly rather than shrinking the reach to nothing.
        float reach = Math.Abs(barrierSize) + entity.width + entity.height;
        return Vector2.DistanceSquared(anomaly.Center, entity.Center) > reach * reach;
    }

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod sots))
            return;

        Type owner = sots.Code.GetType(TargetType);
        MethodInfo accept = owner?.GetMethod("AcceptEntity", BindingFlags.Instance
            | BindingFlags.Public | BindingFlags.DeclaredOnly, binder: null,
            [typeof(Entity), typeof(int)], modifiers: null);
        MethodInfo reject = owner?.GetMethod("RejectEntity", BindingFlags.Instance
            | BindingFlags.Public | BindingFlags.DeclaredOnly, binder: null,
            [typeof(Entity), typeof(float)], modifiers: null);

        Guard(accept, "AcceptEntity", nameof(OutsideAcceptReach), AcceptReach);
        Guard(reject, "RejectEntity", nameof(OutsideRejectReach), barrierArgument: true);
    }

    private void Guard(MethodInfo target, string methodName, string testName,
        float requiredReach = 0f, bool barrierArgument = false)
    {
        if (target == null)
        {
            Mod.Logger.Error($"Void anomaly range guard: {TargetType}.{methodName} is missing, " +
                $"{methodName} left as it is");
            return;
        }

        MethodInfo test = typeof(VoidAnomalyRangeGuard)
            .GetMethod(testName, BindingFlags.Static | BindingFlags.Public);

        MonoModHooks.Modify(target, il =>
        {
            // The reach the guard assumes has to still be written into the method it guards.
            // RejectEntity takes its reach as an argument, so there is no constant to pin.
            if (!barrierArgument && !LoadsConstant(il, requiredReach))
            {
                Mod.Logger.Error($"Void anomaly range guard: {methodName} no longer works to a " +
                    $"reach of {requiredReach}, {methodName} left as it is");
                throw new InvalidOperationException(
                    $"{methodName} no longer works to a reach of {requiredReach}");
            }

            ILCursor cursor = new ILCursor(il);
            ILLabel carryOn = cursor.DefineLabel();

            // The anomaly's own centre comes off the ModProjectile's Projectile, which is the
            // same entity both helpers measure from.
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.Emit(OpCodes.Call, il.Import(typeof(ModProjectile)
                .GetProperty(nameof(ModProjectile.Projectile)).GetMethod));
            cursor.Emit(OpCodes.Ldarg_1);
            if (barrierArgument)
                cursor.Emit(OpCodes.Ldarg_2);

            cursor.Emit(OpCodes.Call, il.Import(test));
            cursor.Emit(OpCodes.Brfalse, carryOn);
            cursor.Emit(OpCodes.Ret);
            cursor.MarkLabel(carryOn);
        });
    }

    private static bool LoadsConstant(ILContext il, float value)
    {
        foreach (Instruction instruction in il.Body.Instructions)
        {
            if (instruction.OpCode == OpCodes.Ldc_R4 && (float)instruction.Operand == value)
                return true;
        }

        return false;
    }
}
