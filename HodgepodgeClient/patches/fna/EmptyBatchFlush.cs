using System.Reflection;
using Microsoft.Xna.Framework.Graphics;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// FNA's SpriteBatch.FlushBatch applies the whole render state before it checks whether there is
// anything to draw:
//
//     private unsafe void FlushBatch()
//     {
//         PrepRenderState();          // blend, sampler, depth, rasterizer, and the shader bind
//         if (numSprites == 0)
//             return;
//
// So a batch that queued nothing still costs a full device state application and an
// Effect.INTERNAL_applyEffect. That is not a rare case: GameInterfaceLayer.Draw wraps every
// interface layer in its own Begin/End pair, and any layer that is active but draws nothing this
// frame pays in full. Across the trace PrepRenderState is 1.005 ms/frame, and applyEffect beneath
// it is 6.84% of the client thread -- almost none of it from mod shaders.
//
// Testing the sprite count first is the same frame, drawn the same way, for every batch that had
// work to do.
//
// What this changes is the empty batch: its End no longer touches the device, so anything relying
// on that flush to leave state behind now sees the previous batch's state. Only raw
// GraphicsDevice primitive draws inherit state that way -- sprites carry their own. Across this
// pack exactly three mod methods draw raw primitives anywhere near a batch boundary, and all three
// open with SpriteSortMode.Immediate, where Begin applies the state and End never reaches
// FlushBatch at all. Vanilla's four raw-draw sites belong to TileBatch and SpriteDrawBuffer, which
// are separate batchers carrying their own state. Adding a mod invalidates that survey.
public class EmptyBatchFlush : Patch
{
    private static FieldInfo _numSprites;

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().EmptyBatchFlush;

    protected override void Apply()
    {
        MethodInfo flushBatch = typeof(SpriteBatch).GetMethod("FlushBatch",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _numSprites = typeof(SpriteBatch).GetField("numSprites",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (flushBatch == null || _numSprites?.FieldType != typeof(int))
        {
            Mod.Logger.Error("Empty batch flush: SpriteBatch.FlushBatch or its numSprites counter " +
                "is missing from this FNA build, patch disabled");
            return;
        }

        MonoModHooks.Modify(flushBatch, TestSpriteCountFirst);
    }

    private void TestSpriteCountFirst(ILContext il)
    {
        // The saving is the PrepRenderState call this returns in front of. If FNA ever reorders
        // the two -- fixing this upstream, or moving state application elsewhere -- an early
        // return here would skip work that is no longer the work being avoided.
        if (il.Body.Instructions is not [_, { } second, ..]
            || !second.MatchCall(out MethodReference prepRenderState)
            || prepRenderState.Name != "PrepRenderState")
        {
            Mod.Logger.Error("Empty batch flush: FlushBatch no longer opens with " +
                "PrepRenderState, patch disabled");
            return;
        }

        ILCursor cursor = new ILCursor(il);
        ILLabel hasSprites = cursor.DefineLabel();
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Ldfld, _numSprites);
        cursor.Emit(OpCodes.Brtrue, hasSprites);
        cursor.Emit(OpCodes.Ret);
        cursor.MarkLabel(hasSprites);
    }
}
