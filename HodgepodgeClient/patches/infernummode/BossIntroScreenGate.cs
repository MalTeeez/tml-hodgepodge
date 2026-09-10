using System;
using System.Reflection;
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
// Neither the update nor the loop consults BossIntroductionAnimationsAreAllowed, so with the
// feature switched off in Infernum's own config the whole thing still runs once a frame -- 0.077
// ms/frame, 0.55% of the client thread, to decide it has nothing to show.
//
// The config only ever suppresses the screens, so with it off this method cannot produce anything.
// Skipping it also skips UpdateScreens, and the animation timers it advances are read nowhere but
// the draw that is being skipped.
public class BossIntroScreenGate : Patch
{
    private const string TargetMod = "InfernumMode";
    private const string ManagerType = "InfernumMode.Content.BossIntroScreens.IntroScreenManager";
    private const string ConfigType = "InfernumMode.Core.InfernumConfig";
    private const string AllowedProperty = "BossIntroductionAnimationsAreAllowed";

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
        ILLabel screensAllowed = cursor.DefineLabel();
        cursor.EmitDelegate<Func<bool>>(AnimationsAllowed);
        cursor.Emit(OpCodes.Brtrue, screensAllowed);
        cursor.Emit(OpCodes.Ret);
        cursor.MarkLabel(screensAllowed);
    }

    private static bool AnimationsAllowed() => _animationsAllowed();
}
