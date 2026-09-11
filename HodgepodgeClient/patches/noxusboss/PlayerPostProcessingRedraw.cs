using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// NoxusBoss binds a render target, clears it and draws the player again -- armour, accessories,
// wings, held item -- for every active player every frame, and only then asks whether any shader
// wanted it. ApplyAllPostProcessingEffects returns immediately when the player has no effects,
// which is almost always. Move the redraw behind that existing gate rather than duplicating the
// gate's condition, which would mean reading the effect list through five reflected members per
// player per frame.
public class PlayerPostProcessingRedraw : Patch
{
    private const string ShaderSystemTypeName =
        "NoxusBoss.Core.Graphics.Players.PlayerPostProcessingShaderSystem";

    private static readonly string[] RedrawCalls =
    [
        "get_GraphicsDevice", "get_PlayerTargets", "get_Item", "op_Implicit", "SetRenderTarget",
        "get_Transparent", "Clear", ".ctor", "DrawPlayers"
    ];

    private static PropertyInfo _playerTargets;
    private static MethodInfo _toRenderTarget;
    private static bool _redrawRelocated;

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().PlayerPostProcessingRedraw;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod("NoxusBoss", out Mod noxusBoss))
            return;

        Type shaderSystem = noxusBoss.Code.GetType(ShaderSystemTypeName);
        MethodInfo updateTargets = shaderSystem?.GetMethod("UpdateTargets",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo applyEffects = shaderSystem?.GetMethod("ApplyAllPostProcessingEffects",
            BindingFlags.Static | BindingFlags.NonPublic);
        _playerTargets = shaderSystem?.GetProperty("PlayerTargets",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        // Luminance's render target wrapper converts to the FNA type through an implicit operator,
        // and defines more than one, so pick it by return type rather than by argument.
        Type managedTarget = _playerTargets?.PropertyType.IsGenericType == true
            ? _playerTargets.PropertyType.GetGenericArguments()[^1]
            : null;
        _toRenderTarget = managedTarget?.GetMethods(BindingFlags.Static | BindingFlags.Public)
            .FirstOrDefault(method => method.Name == "op_Implicit"
                && method.ReturnType == typeof(RenderTarget2D));

        if (updateTargets == null || applyEffects == null || _toRenderTarget == null)
        {
            Mod.Logger.Error("Player post processing redraw: UpdateTargets, " +
                $"ApplyAllPostProcessingEffects or PlayerTargets missing from " +
                $"{ShaderSystemTypeName}, patch disabled");
            return;
        }

        // Give the redraw its new home before taking away the old one. The reverse order leaves
        // the shaders sampling a target nothing ever drew into. The flag is static, so it has to
        // be cleared first or it answers for the previous load and the removal happens anyway.
        _redrawRelocated = false;
        MonoModHooks.Modify(applyEffects, RelocateRedraw);
        if (_redrawRelocated)
            MonoModHooks.Modify(updateTargets, RemoveUnconditionalRedraw);
    }

    // Redraw once the effect count is known to be non-zero, immediately before the shader passes
    // that read the target.
    private void RelocateRedraw(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.Before,
                i => i.MatchLdsfld(out FieldReference field) && field.Name == "instance",
                i => i.MatchCallvirt(out MethodReference called)
                    && called.Name == "get_GraphicsDevice"))
        {
            Mod.Logger.Error("Player post processing redraw: graphics device anchor not found " +
                "in ApplyAllPostProcessingEffects, patch disabled");
            return;
        }

        cursor.Emit(OpCodes.Ldarg_0);
        cursor.EmitDelegate<Action<Player>>(RedrawPlayerToTarget);
        _redrawRelocated = true;
    }

    // Drop the target bind, the clear and the redraw from the per-player loop, leaving it to call
    // ApplyAllPostProcessingEffects and store the result exactly as before.
    private void RemoveUnconditionalRedraw(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.After,
                i => i.MatchCall(out MethodReference called) && called.Name == "get_Current",
                i => i.MatchStloc(out _)))
        {
            Mod.Logger.Error("Player post processing redraw: loop body start not found in " +
                "UpdateTargets, redraw left in place");
            return;
        }
        int redrawStart = cursor.Index;

        if (!cursor.TryGotoNext(MoveType.Before,
                i => i.MatchCall(out MethodReference called)
                    && called.Name == "ApplyAllPostProcessingEffects"))
        {
            Mod.Logger.Error("Player post processing redraw: ApplyAllPostProcessingEffects call " +
                "not found in UpdateTargets, redraw left in place");
            return;
        }

        // The instruction before the call pushes the player, and has to survive.
        int redrawEnd = cursor.Index - 1;

        if (!HoldsOnlyTheRedraw(il, redrawStart, redrawEnd))
        {
            Mod.Logger.Error("Player post processing redraw: the loop body no longer holds only " +
                "the bind, clear and redraw, redraw left in place");
            return;
        }

        cursor.Index = redrawStart;
        cursor.RemoveRange(redrawEnd - redrawStart);
    }

    // Deleting a range rather than named instructions means the range has to be proven first. Every
    // call the bind, clear and redraw make, in order: anything else in there is work added since,
    // which this patch has no business removing.
    private static bool HoldsOnlyTheRedraw(ILContext il, int start, int end) =>
        il.Body.Instructions.Skip(start).Take(end - start)
            .Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => ((MethodReference)instruction.Operand).Name)
            .SequenceEqual(RedrawCalls);

    private static void RedrawPlayerToTarget(Player player)
    {
        object playerTarget = ((IDictionary)_playerTargets.GetValue(null))[player.whoAmI];
        GraphicsDevice graphicsDevice = Main.instance.GraphicsDevice;
        graphicsDevice.SetRenderTarget((RenderTarget2D)_toRenderTarget.Invoke(null, [playerTarget]));
        graphicsDevice.Clear(Color.Transparent);
        Main.PlayerRenderer.DrawPlayers(Main.Camera, [player]);
    }
}
