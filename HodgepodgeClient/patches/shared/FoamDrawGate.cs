using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework.Graphics;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// SOTS and Catalyst each draw a foam effect around the player and around a few of that player's
// projectiles, from the same two static helpers with the same defect. DrawPlayerFoam runs once per
// player per frame, walks all 1000 projectile slots resolving two or three ModContent ids per slot,
// and calls DrawFoam several times. DrawFoam asks the asset repository for its texture by path
// before it looks at the list it was given:
//
//     Texture2D val = ModContent.Request<Texture2D>("SOTS/Assets/PlayerCurseFoam", ImmediateLoad);
//     for (int i = 0; i < dustList.Count; i++) { ... }
//
// and that list is empty in every scene without the effect, which is nearly all of them. On the
// lower-end client the SOTS helper was 0.55% of the thread and the Catalyst one 0.12%, all of it
// repository lookups feeding loops that ran zero times.
//
// Three changes, none of which can draw anything different: DrawFoam returns before the lookup when
// the list is empty, the lookup itself is answered from a table, and the projectile ids in
// DrawPlayerFoam fold to the constants they resolve to.
public abstract class FoamDrawGate : Patch
{
    protected abstract string TargetMod { get; }

    protected abstract string TargetType { get; }

    protected abstract string Label { get; }

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod target))
            return;

        Type helper = target.Code.GetType(TargetType);
        MethodInfo drawFoam = helper?.GetMethod("DrawFoam",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly);
        MethodInfo drawPlayerFoam = helper?.GetMethod("DrawPlayerFoam",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly, binder: null,
            [typeof(SpriteBatch), typeof(Player)], modifiers: null);

        if (drawPlayerFoam == null || !TakesFoamList(drawFoam))
        {
            Mod.Logger.Error($"{Label}: {TargetType}.DrawFoam(List, int, SpriteBatch) or " +
                "DrawPlayerFoam(SpriteBatch, Player) is missing, patch disabled");
            return;
        }

        MonoModHooks.Modify(drawFoam, SkipEmptyList);
        MonoModHooks.Modify(drawPlayerFoam, il =>
        {
            List<string> skipped = [];
            int folded = ContentIdFolding.FoldContentLookups(il,
                ContentIdFolding.InAssembly(target.Code), skipped.Add);

            ContentIdFolding.Report(Mod, Label, "DrawPlayerFoam", folded, skipped);
        });
    }

    // The list's element type differs between the two mods and never needs naming: the gate only
    // asks whether it is empty.
    private static bool TakesFoamList(MethodInfo drawFoam) =>
        drawFoam?.GetParameters() is [{ ParameterType: var list }, { ParameterType: var layer },
            { ParameterType: var batch }]
        && list.IsGenericType && list.GetGenericTypeDefinition() == typeof(List<>)
        && layer == typeof(int) && batch == typeof(SpriteBatch);

    // `if (dustList == null || dustList.Count == 0) return;` in front of the body. A null list is
    // treated as empty rather than reaching the throw the original would hit on its first read.
    private void SkipEmptyList(ILContext il)
    {
        MethodInfo count = typeof(ICollection).GetProperty(nameof(ICollection.Count)).GetMethod;
        ILCursor cursor = new ILCursor(il);
        ILLabel nothingToDraw = cursor.DefineLabel();
        ILLabel draw = cursor.DefineLabel();

        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Brfalse, nothingToDraw);
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Callvirt, count);
        cursor.Emit(OpCodes.Brtrue, draw);
        cursor.MarkLabel(nothingToDraw);
        cursor.Emit(OpCodes.Ret);
        cursor.MarkLabel(draw);

        // The gate stands on its own; the table only matters for the calls that remain.
        if (TextureRequestCache.RewriteRequests(il) == 0)
            Mod.Logger.Error($"{Label}: DrawFoam no longer requests its texture by name, only " +
                "the empty list gate applied");
    }
}
