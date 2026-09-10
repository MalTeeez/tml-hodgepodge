using System;
using System.Reflection;
using Microsoft.Xna.Framework.Graphics;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// LootBeams draws its beams from GlobalItem.PreDrawInWorld, which lands in the middle of vanilla's
// item pass. To get additive blending it ends the running batch and begins its own, then ends that
// and begins the pass back -- both with SpriteSortMode.Immediate, while Main.DrawItems opened the
// pass as Deferred.
//
// Immediate flushes to the GPU on every Draw, so each beam costs three draw calls instead of one
// batch. Worse, StopAdditive hands the pass back in Immediate rather than the Deferred it borrowed,
// so from the first beamed item onwards every remaining item sprite in the frame becomes its own
// draw call too, beamed or not.
//
// Neither batch supplies an Effect, which is the only thing Immediate is for. Deferred preserves
// submission order within a batch and the End calls preserve it across batches, so the frame comes
// out the same -- and the pass is handed back in the mode vanilla opened it with.
public class LootBeamBatchMode : Patch
{
    private const string TargetMod = "LootBeams";
    private const string TargetType = "LootBeams.LootBeamItem";

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().LootBeamBatchMode;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod lootBeams))
            return;

        Type beamItem = lootBeams.Code.GetType(TargetType);
        MethodInfo startAdditive = FindBatchSwitch(beamItem, "StartAdditive");
        MethodInfo stopAdditive = FindBatchSwitch(beamItem, "StopAdditive");

        if (startAdditive == null || stopAdditive == null)
        {
            Mod.Logger.Error("Loot beam batch mode: StartAdditive or StopAdditive missing from " +
                $"{TargetType}, patch disabled");
            return;
        }

        MonoModHooks.Modify(startAdditive, DeferTheBatch);
        MonoModHooks.Modify(stopAdditive, DeferTheBatch);
    }

    private static MethodInfo FindBatchSwitch(Type beamItem, string name) =>
        beamItem?.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic, binder: null,
            new[] { typeof(SpriteBatch) }, modifiers: null);

    // Anchored on the sort mode being the argument immediately before the blend state, which is
    // what pins the rewrite to Begin's first argument rather than to any other constant nearby.
    // The two methods have the same shape, so both are edited by this one manipulator.
    private void DeferTheBatch(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.Before,
                i => i.MatchLdcI4((int)SpriteSortMode.Immediate),
                i => i.MatchLdsfld(out FieldReference blendState)
                    && blendState.FieldType.FullName == typeof(BlendState).FullName))
        {
            Mod.Logger.Error($"Loot beam batch mode: {il.Method.Name} no longer opens an " +
                "immediate batch, left as it is");
            return;
        }

        cursor.Next.OpCode = OpCodes.Ldc_I4_0;
        cursor.Next.Operand = null;
    }
}
