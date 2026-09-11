using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework.Graphics;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// Calamity and Luminance each keep a set of half resolution render targets that pixelated
// primitives draw into, one per draw layer, and each frame both systems do the same two things.
// Before the frame, every target is bound and cleared to transparent, and drawn into only if some
// NPC or projectile registered for that layer:
//
//     using (renderTarget.Scope(preserveContents: true, Color.Transparent))
//     {
//         if (!pixelPrimitives.Any())
//             return;
//
// Then at each layer's point in the frame the target is drawn over the screen at twice its size,
// between a Begin and an End of its own:
//
//     Main.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, ...);
//     Main.spriteBatch.Draw(target, Vector2.Zero, null, Color.White, 0f, Vector2.Zero, 2f, ...);
//     Main.spriteBatch.End();
//
// Nine layers in Calamity, four in Luminance, and on a frame with no pixelated primitives -- which
// is every frame outside a handful of boss attacks -- all thirteen draw a transparent texture. The
// cost is not the draw; it is thirteen batch restarts, each applying the sprite shader and render
// state, for 0.55% of the lower-end client's thread.
//
// The first half records which targets it left empty, and the second half skips the batch for a
// target it knows to be empty. The record is keyed on the target object itself and dropped when a
// resize replaces it, so an unrecorded target is drawn, which is the safe direction.
//
// What the skipped batch would have left behind is the render state its End applied. Anything
// drawing raw primitives off inherited state after one of these thirteen points would now see the
// state of whatever batch ran before instead. Vanilla begins every batch with explicit state, so
// the exposure is to a mod that does not, in the frames where nothing pixelated is alive.
//
// Read against CalamityMod 2.2.4 and Luminance 1.0.14.
public class EmptyPixelationTargetDraw : Patch
{
    private const string CalamitySystem = "CalamityMod.Graphics.Primitives.PrimitivePixelationSystem";
    private const string LuminanceSystem = "Luminance.Core.Graphics.PrimitivePixelationSystem";
    private const string LuminanceTarget = "Luminance.Core.Graphics.ManagedRenderTarget";

    private static readonly ConditionalWeakTable<RenderTarget2D, object> EmptyTargets = new();
    private static readonly object Empty = new();

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().EmptyPixelationTargetDraw;

    // Runs on entry to DrawPrimsToRenderTarget, before the target is cleared, with the collections
    // the method is about to test. Luminance also queues one-off draw actions per layer; Calamity
    // passes null there.
    public static void Record(RenderTarget2D target, ICollection primitives, ICollection actions)
    {
        if (target == null)
            return;

        if (primitives.Count == 0 && (actions == null || actions.Count == 0))
            EmptyTargets.AddOrUpdate(target, Empty);
        else
            EmptyTargets.Remove(target);
    }

    public static bool IsKnownEmpty(RenderTarget2D target) =>
        target != null && EmptyTargets.TryGetValue(target, out _);

    protected override void Apply()
    {
        if (ModLoader.TryGetMod("CalamityMod", out Mod calamity))
        {
            Type system = calamity.Code.GetType(CalamitySystem);
            Install("Calamity", system, primitivesArg: 2, actionsArg: null,
                Declared(system, "ReturnAssociatedRenderTarget"));
        }

        if (ModLoader.TryGetMod("Luminance", out Mod luminance))
        {
            Type managed = luminance.Code.GetType(LuminanceTarget);
            Install("Luminance", luminance.Code.GetType(LuminanceSystem), primitivesArg: 1,
                actionsArg: 2, managed?.GetMethod("op_Implicit",
                    BindingFlags.Static | BindingFlags.Public, binder: null, [managed],
                    modifiers: null));
        }
    }

    // `toTarget` turns DrawTargetScaled's one argument into the RenderTarget2D it draws, which is
    // a layer enum in Calamity and a wrapper type in Luminance.
    private void Install(string label, Type system, int primitivesArg, int? actionsArg,
        MethodInfo toTarget)
    {
        MethodInfo drawPrims = Declared(system, "DrawPrimsToRenderTarget");
        MethodInfo drawTarget = Declared(system, "DrawTargetScaled");

        if (!TakesTargetAndCollections(drawPrims, primitivesArg, actionsArg)
            || drawTarget?.GetParameters() is not [{ ParameterType: var layer }]
            || toTarget?.ReturnType != typeof(RenderTarget2D)
            || toTarget.GetParameters() is not [{ ParameterType: var accepted }]
            || !accepted.IsAssignableFrom(layer))
        {
            Mod.Logger.Error($"Empty pixelation target draw: {label}'s pixelation system no " +
                "longer has the DrawPrimsToRenderTarget and DrawTargetScaled this patch was " +
                "written against, patch disabled");
            return;
        }

        // The record goes in first: a record nobody reads costs nothing, while a skip with no
        // record behind it would never fire, so a failure between the two leaves the mod as it is.
        bool recorded = false;
        MonoModHooks.Modify(drawPrims, il => recorded = RecordContents(il, label, primitivesArg,
            actionsArg));
        if (recorded)
            MonoModHooks.Modify(drawTarget, il => SkipKnownEmpty(il, label, toTarget));
    }

    private static MethodInfo Declared(Type owner, string name) => owner?.GetMethod(name,
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

    private static bool TakesTargetAndCollections(MethodInfo drawPrims, int primitivesArg,
        int? actionsArg)
    {
        ParameterInfo[] parameters = drawPrims?.GetParameters();
        if (parameters == null || parameters[0].ParameterType != typeof(RenderTarget2D))
            return false;

        return IsCollection(parameters, primitivesArg)
            && (actionsArg == null || IsCollection(parameters, actionsArg.Value));
    }

    private static bool IsCollection(ParameterInfo[] parameters, int index) =>
        index < parameters.Length
        && typeof(ICollection).IsAssignableFrom(parameters[index].ParameterType);

    // The record says a target is empty when the method is about to find nothing to draw, which
    // is only the same thing while the method still tests its lists before drawing.
    private bool RecordContents(ILContext il, string label, int primitivesArg, int? actionsArg)
    {
        if (!il.Body.Instructions.Any(instruction => instruction.MatchCall(out MethodReference any)
                && any.Name == nameof(Enumerable.Any) && any.DeclaringType.Name == nameof(Enumerable)))
        {
            Mod.Logger.Error($"Empty pixelation target draw: {label}'s DrawPrimsToRenderTarget " +
                "no longer tests for an empty layer before drawing, patch disabled");
            return false;
        }

        ILCursor cursor = new ILCursor(il);
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(LoadArgument(primitivesArg));
        cursor.Emit(actionsArg == null ? OpCodes.Ldnull : LoadArgument(actionsArg.Value));
        cursor.Emit(OpCodes.Call, typeof(EmptyPixelationTargetDraw).GetMethod(nameof(Record)));
        return true;
    }

    // Skipping the body is only a no-op while the body is still one batch around one draw. A
    // second draw, or a third batch call, would mean it now does something this patch has not read.
    private void SkipKnownEmpty(ILContext il, string label, MethodInfo toTarget)
    {
        int begins = il.Body.Instructions.Count(instruction => IsSpriteBatchCall(instruction, "Begin"));
        int ends = il.Body.Instructions.Count(instruction => IsSpriteBatchCall(instruction, "End"));
        int draws = il.Body.Instructions.Count(instruction => IsSpriteBatchCall(instruction, "Draw"));
        if (begins != 1 || ends != 1 || draws != 1)
        {
            Mod.Logger.Error($"Empty pixelation target draw: {label}'s DrawTargetScaled is no " +
                $"longer one batch around one draw ({begins} begins, {ends} ends, {draws} " +
                "draws), the draw is left as it is");
            return;
        }

        ILCursor cursor = new ILCursor(il);
        ILLabel draw = cursor.DefineLabel();
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Call, toTarget);
        cursor.Emit(OpCodes.Call, typeof(EmptyPixelationTargetDraw).GetMethod(nameof(IsKnownEmpty)));
        cursor.Emit(OpCodes.Brfalse, draw);
        cursor.Emit(OpCodes.Ret);
        cursor.MarkLabel(draw);
    }

    private static OpCode LoadArgument(int index) => index switch
    {
        1 => OpCodes.Ldarg_1,
        2 => OpCodes.Ldarg_2,
        3 => OpCodes.Ldarg_3,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    private static bool IsSpriteBatchCall(Instruction instruction, string name) =>
        instruction.MatchCallvirt(out MethodReference called)
        && called.Name == name && called.DeclaringType.Name == nameof(SpriteBatch);
}
