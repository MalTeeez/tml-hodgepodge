using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework.Graphics;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// CosmosMetaball draws the black hole shade behind Goozma's Stellar Black Hole. It is hooked on
// DoDraw_DrawNPCsOverTiles with no boss check, so every frame of every scene it sets a dozen
// shader parameters, restarts the batch with the Cosmos effect, requests its render target --
// which next frame binds and clears the target, walks all 1000 projectile slots resolving
// ProjectileType<StellarBlackHole>() per slot, and draws its particles into it -- and covers the
// screen with the result:
//
//     content.Request();
//     if (content.IsReady)
//         Main.spriteBatch.Draw((Texture2D)content.GetTarget(), Vector2.Zero, ...);
//
// 0.3% of the lower-end client's thread, 0.06 ms/frame, in scenes where the target is empty.
//
// Two things make skipping all of it exact while nothing draws into the target. The Cosmos shader
// returns `result * smoothstep(0, 0.0001, length(screen)) + edge`, and both terms are zero where
// the target and its neighbouring texels are transparent, so the fullscreen draw of an empty
// target paints nothing. And vanilla's ARenderTargetContentByRequest marks itself not ready on
// any frame it was not requested, so the first frame a black hole appears finds IsReady false and
// draws nothing, exactly as the original does on that frame with the previous frame's empty
// target. The particle system's update runs from its own hook and is untouched.
//
// The pixel shader was read against CalamityHunt 1.2.3's CosmosEffect.fx; the code against 1.2.3.
public class CosmosMetaballIdleDraw : Patch
{
    private const string TargetMod = "CalamityHunt";
    private const string TargetType = "CalamityHunt.Common.Graphics.RenderTargets.CosmosMetaball";

    private static int _blackHole;
    private static ICollection[] _particlePools;

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().CosmosMetaballIdleDraw;

    public static bool NothingToDraw()
    {
        foreach (Projectile projectile in Main.ActiveProjectiles)
        {
            if (projectile.type == _blackHole)
                return false;
        }

        foreach (ICollection pool in _particlePools)
        {
            if (pool.Count > 0)
                return false;
        }

        return true;
    }

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod hunt))
            return;

        Type cosmos = hunt.Code.GetType(TargetType);
        MethodInfo drawTarget = cosmos?.GetMethod("DrawTarget",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        // Initialize's lambdas: the delegate that draws the target content, and the predicate
        // picking the black hole projectiles it draws, both compiled onto a nested closure class.
        MethodInfo[] lambdas = cosmos == null ? [] : cosmos.GetMethods(BindingFlags.Instance
                | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
                | BindingFlags.DeclaredOnly)
            .Concat(cosmos.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
                .SelectMany(closure => closure.GetMethods(BindingFlags.Instance
                    | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)))
            .Where(method => method.Name.StartsWith("<Initialize>b__")).ToArray();
        MethodInfo targetContent = lambdas.FirstOrDefault(method =>
            method.GetParameters() is [{ ParameterType: var batch }] && batch == typeof(SpriteBatch));
        object renderer = cosmos?.GetProperty("Particles", BindingFlags.Static | BindingFlags.Public)
            ?.GetValue(null);

        if (drawTarget?.GetParameters() is not [{ ParameterType: var hook }, { ParameterType: var main }]
            || main != typeof(Main) || targetContent == null || renderer == null
            || !hunt.TryFind("StellarBlackHole", out ModProjectile blackHole))
        {
            Mod.Logger.Error($"Cosmos metaball idle draw: {TargetType}.DrawTarget(orig, Main), " +
                "its target content lambda, its Particles renderer or StellarBlackHole is " +
                "missing, patch disabled");
            return;
        }

        // The skip is exact only while the target holds nothing but black hole quads and the
        // renderer's particles, and while the ready flag still gates the fullscreen draw.
        if (!lambdas.Any(lambda => Calls(lambda, "ProjectileType")) || !Calls(targetContent, "Draw")
            || !Calls(drawTarget, "Request") || !Calls(drawTarget, "get_IsReady"))
        {
            Mod.Logger.Error("Cosmos metaball idle draw: the target content or DrawTarget no " +
                "longer has the shape this patch was written against, patch disabled");
            return;
        }

        // The renderer keeps its particles in lists it concatenates on demand; counting the lists
        // directly asks the same question without an enumerator per frame.
        _particlePools = renderer.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(field => typeof(ICollection).IsAssignableFrom(field.FieldType))
            .Select(field => (ICollection)field.GetValue(renderer))
            .Where(pool => pool != null).ToArray();

        if (_particlePools.Length == 0)
        {
            Mod.Logger.Error("Cosmos metaball idle draw: the particle renderer keeps no " +
                "collections to count, patch disabled");
            return;
        }

        _blackHole = blackHole.Type;
        MethodInfo invoke = hook.GetMethod("Invoke");
        MonoModHooks.Modify(drawTarget, il => SkipWhenEmpty(il, invoke));
    }

    private static bool Calls(MethodBase method, string name) => InstructionsOf(method).Any(
        instruction => (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
            && instruction.Operand is MethodReference called && called.Name == name);

    // `if (NothingToDraw()) { orig(self); return; }` in front of the body.
    private static void SkipWhenEmpty(ILContext il, MethodInfo invoke)
    {
        ILCursor cursor = new ILCursor(il);
        ILLabel draw = cursor.DefineLabel();
        cursor.Emit(OpCodes.Call, typeof(CosmosMetaballIdleDraw).GetMethod(nameof(NothingToDraw)));
        cursor.Emit(OpCodes.Brfalse, draw);
        cursor.Emit(OpCodes.Ldarg_1);
        cursor.Emit(OpCodes.Ldarg_2);
        cursor.Emit(OpCodes.Callvirt, invoke);
        cursor.Emit(OpCodes.Ret);
        cursor.MarkLabel(draw);
    }
}
