using System;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// Infernum inserts a "Boss Introduction Screens" interface layer, and its draw is:
//
//     public static void Draw()
//     {
//         UpdateScreens();                                    // every screen, every frame
//         foreach (BaseIntroScreen screen in IntroScreens)
//             if (screen.ShouldBeActive() && !BossRushActive) { ... }
//     }
//
// The loop does not consult BossIntroductionAnimationsAreAllowed, so with the feature switched off
// in Infernum's own config it still runs once a frame to decide it has nothing to show.
//
// The config only ever suppresses the screens, so with it off the loop cannot produce anything and
// is skipped. UpdateScreens is deliberately left to run first. Its per screen Update is what
// resets the animation state while the config is off:
//
//     if (!ShouldBeActive() || !InfernumConfig.Instance.BossIntroductionAnimationsAreAllowed)
//     {
//         AnimationTimer = 0; HasPlayedMainSound = false; CachedText = string.Empty; return;
//     }
//
// Skipping that would freeze a part played animation, so switching the setting back on during an
// encounter would resume mid animation with its sound already marked as played.
//
// UpdateScreens is the larger half of the cost, 558 ms of the 882 ms this method spent across a
// 240 second capture, and nearly all of that is the ShouldBeActive call the reset short circuits
// on. It cannot be skipped: ModCallIntroScreen.ShouldBeActive returns a delegate handed in by
// another mod through Mod.Call, so no audit here can establish that it is free of side effects.
// What is left to reclaim is the draw loop, 324 ms of the 882 ms.
public class BossIntroScreenGate : Patch
{
    private const string TargetMod = "InfernumMode";
    private const string ManagerType = "InfernumMode.Content.BossIntroScreens.IntroScreenManager";
    private const string ConfigType = "InfernumMode.Core.InfernumConfig";
    private const string AllowedProperty = "BossIntroductionAnimationsAreAllowed";
    private const string UpdateMethod = "UpdateScreens";

    private static Func<bool> _animationsAllowed;

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().BossIntroScreenGate;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod infernum))
            return;

        MethodInfo draw = infernum.Code.GetType(ManagerType)?.GetMethod("Draw",
            BindingFlags.Static | BindingFlags.Public, binder: null, Type.EmptyTypes,
            modifiers: null);
        Type config = infernum.Code.GetType(ConfigType);
        MethodInfo allowed = config?.GetProperty(AllowedProperty)?.GetMethod;
        object instance = config?.GetProperty("Instance",
            BindingFlags.Static | BindingFlags.Public)?.GetMethod?.Invoke(null, null);

        if (draw == null || allowed?.ReturnType != typeof(bool) || instance == null)
        {
            Mod.Logger.Error($"Boss intro screen gate: {ManagerType}.Draw or a bool " +
                $"{ConfigType}.{AllowedProperty} is missing, patch disabled");
            return;
        }

        // Bound to the config object rather than re-fetched, because tModLoader edits that object
        // in place -- so this still reads the current setting if it is changed while playing.
        _animationsAllowed = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), instance,
            allowed);
        MonoModHooks.Modify(draw, SkipWhenScreensAreOff);
    }

    private void SkipWhenScreensAreOff(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);

        // After the UpdateScreens call, never before it, so the animation reset still happens.
        if (!cursor.TryGotoNext(MoveType.After,
                i => i.MatchCall(out MethodReference called) && called.Name == UpdateMethod))
        {
            Mod.Logger.Error($"Boss intro screen gate: Draw no longer opens with {UpdateMethod}, " +
                "so the gate cannot be placed after the animation reset, patch disabled");
            return;
        }

        ILLabel screensAllowed = cursor.DefineLabel();
        cursor.EmitDelegate<Func<bool>>(AnimationsAllowed);
        cursor.Emit(OpCodes.Brtrue, screensAllowed);
        cursor.Emit(OpCodes.Ret);
        cursor.MarkLabel(screensAllowed);
    }

    private static bool AnimationsAllowed() => _animationsAllowed();
}
