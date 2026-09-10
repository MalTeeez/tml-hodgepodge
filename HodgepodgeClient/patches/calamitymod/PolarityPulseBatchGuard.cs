using System.Collections;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// CalamityPolarityNPC is a GlobalNPC with InstancePerEntity, and its PostDraw restarts the sprite
// batch twice for every NPC drawn, every frame:
//
//     Main.spriteBatch.End();
//     Main.spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.Additive, ...);
//     foreach (Particle pulse in pulses) { ... }
//     Main.spriteBatch.End();
//     Main.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, ...);
//
// The only early exit is for an unrelated MiracleBlight flag, so the restarts happen whether or not
// the NPC has any pulses -- and it almost never does. Measured, the method is 98.4% SpriteBatch.End
// and 0.0% its own code: all batch churn, no drawing. It is the largest single caller of
// SpriteBatch.End in the trace, ahead of Main.DrawDust.
//
// Returning before the first End when the list is empty is exactly equivalent. The batch it
// restores -- Deferred, AlphaBlend, DefaultSamplerState, None, Rasterizer, no effect, and
// Main.GameViewMatrix.TransformationMatrix -- is the one Main.DoDraw_DrawNPCsOverTiles opened,
// because Main.Transform is defined as GameViewMatrix.TransformationMatrix. Nothing is flushed
// early either, and a deferred batch draws in submission order regardless.
public class PolarityPulseBatchGuard : Patch
{
    private const string TargetMod = "CalamityMod";
    private const string TargetType = "CalamityMod.NPCs.CalamityPolarityNPC";
    private const string PulsesField = "pulses";

    private static FieldInfo _pulses;

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().PolarityPulseBatchGuard;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod calamity))
            return;

        System.Type polarity = calamity.Code.GetType(TargetType);
        MethodInfo postDraw = polarity?.GetMethod("PostDraw",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        _pulses = polarity?.GetField(PulsesField, BindingFlags.Instance | BindingFlags.Public);

        if (postDraw == null || _pulses == null || !typeof(ICollection).IsAssignableFrom(_pulses.FieldType))
        {
            Mod.Logger.Error($"Polarity pulse batch guard: {TargetType}.PostDraw or a collection " +
                $"{PulsesField} is missing, patch disabled");
            return;
        }

        MonoModHooks.Modify(postDraw, SkipWhenNoPulses);
    }

    private void SkipWhenNoPulses(ILContext il)
    {
        // Skipping the body is only equivalent while the body is still the pair of batch restarts
        // described above. Two ends and two begins is what makes it a no-op with no pulses; a
        // third of either would mean it now leaves the batch somewhere this patch has not read.
        int ends = il.Body.Instructions.Count(i => IsSpriteBatchCall(i, "End"));
        int begins = il.Body.Instructions.Count(i => IsSpriteBatchCall(i, "Begin"));
        if (ends != 2 || begins != 2)
        {
            Mod.Logger.Error("Polarity pulse batch guard: PostDraw no longer restarts the batch " +
                $"exactly twice ({ends} ends, {begins} begins), patch disabled");
            return;
        }

        // Read through ICollection so the element type never has to be named, and treat a null
        // list as empty rather than reaching the throw the original would have hit later anyway.
        MethodInfo count = typeof(ICollection).GetProperty(nameof(ICollection.Count)).GetMethod;
        ILCursor cursor = new ILCursor(il);
        ILLabel nothingToDraw = cursor.DefineLabel();
        ILLabel drawPulses = cursor.DefineLabel();

        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Ldfld, _pulses);
        cursor.Emit(OpCodes.Brfalse, nothingToDraw);
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Ldfld, _pulses);
        cursor.Emit(OpCodes.Callvirt, count);
        cursor.Emit(OpCodes.Brtrue, drawPulses);
        cursor.MarkLabel(nothingToDraw);
        cursor.Emit(OpCodes.Ret);
        cursor.MarkLabel(drawPulses);
    }

    private static bool IsSpriteBatchCall(Instruction instruction, string name) =>
        instruction.MatchCallvirt(out MethodReference called)
        && called.Name == name && called.DeclaringType.Name == "SpriteBatch";
}
