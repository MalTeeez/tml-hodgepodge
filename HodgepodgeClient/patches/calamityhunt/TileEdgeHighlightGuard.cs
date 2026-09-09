using System;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// CalamityHunt's TileEdgeHighlight rebuilds its overlay render target every frame -- swapping
// targets, redrawing every player and walking the visible tile grid -- but only draws the result
// while StellarGeliath is mid-attack. Give the producer the consumer's fade guard.
public class TileEdgeHighlightGuard : Patch
{
    private const string HighlightTypeName = "CalamityHunt.Common.Systems.TileEdgeHighlight";

    private static FieldInfo _fadeField;

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().TileEdgeHighlightGuard;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod("CalamityHunt", out Mod calamityHunt))
            return;

        Type highlightType = calamityHunt.Code.GetType(HighlightTypeName);
        MethodInfo combineTileTargets = highlightType?.GetMethod("CombineTileTargets",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _fadeField = highlightType?.GetField("_fade", BindingFlags.Instance | BindingFlags.NonPublic);

        if (combineTileTargets == null || _fadeField == null)
        {
            Mod.Logger.Error("Tile edge highlight guard: CombineTileTargets or _fade missing " +
                $"from {HighlightTypeName}, patch disabled");
            return;
        }

        MonoModHooks.Modify(combineTileTargets, InjectFadeGuard);
    }

    // Return immediately after the mod's own orig.Invoke(), which is the only work below the
    // injection point that anything outside the overlay depends on.
    private void InjectFadeGuard(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.After,
                i => i.MatchCallvirt(out MethodReference called) && called.Name == "Invoke"))
        {
            Mod.Logger.Error("Tile edge highlight guard: orig.Invoke() anchor not found in " +
                "CombineTileTargets, patch disabled");
            return;
        }

        ILLabel buildOverlay = cursor.DefineLabel();
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.EmitDelegate<Func<object, bool>>(OverlayIsInvisible);
        cursor.Emit(OpCodes.Brfalse, buildOverlay);
        cursor.Emit(OpCodes.Ret);
        cursor.MarkLabel(buildOverlay);
    }

    // The same threshold TileEdgeHighlight.DrawHighlight tests before drawing the target.
    private static bool OverlayIsInvisible(object tileEdgeHighlight) =>
        (float)_fadeField.GetValue(tileEdgeHighlight) <= 0.01f;
}
