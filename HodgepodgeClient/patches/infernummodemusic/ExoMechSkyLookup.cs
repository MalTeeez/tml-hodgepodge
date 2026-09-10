using System;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// ExoMechMusicHandler.SkyActive is one expression:
//
//     (bool)Calamity.Code.GetType("CalamityMod.Skies.ExoMechsSky")
//         .GetProperty("CanSkyBeActive").GetValue(null)
//
// IsSceneEffectActive reads it once per tick, so every tick pays an Assembly.GetType name lookup
// across the whole of Calamity, a GetProperty lookup and a reflective invoke, to arrive at a
// static bool. None of those handles can change once mods have finished loading.
//
// Resolve the property once at load and answer from a delegate bound straight to its getter. The
// lookups disappear and the value is the one the reflection would have returned.
public class ExoMechSkyLookup : Patch
{
    private const string TargetMod = "InfernumModeMusic";
    private const string HandlerTypeName = "InfernumModeMusic.MusicOverrides.ExoMechMusicHandler";
    private const string SkyTypeName = "CalamityMod.Skies.ExoMechsSky";
    private const string SkyPropertyName = "CanSkyBeActive";

    private static Func<bool> _canSkyBeActive;

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().ExoMechSkyLookup;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod infernumMusic)
            || !ModLoader.TryGetMod("CalamityMod", out Mod calamity))
            return;

        MethodInfo skyActive = infernumMusic.Code.GetType(HandlerTypeName)
            ?.GetProperty("SkyActive", BindingFlags.Static | BindingFlags.Public)?.GetMethod;
        MethodInfo canSkyBeActive = calamity.Code.GetType(SkyTypeName)
            ?.GetProperty(SkyPropertyName, BindingFlags.Static | BindingFlags.Public)?.GetMethod;

        if (skyActive?.ReturnType != typeof(bool) || canSkyBeActive?.ReturnType != typeof(bool))
        {
            Mod.Logger.Error($"Exo mech sky lookup: {HandlerTypeName}.SkyActive or {SkyTypeName}." +
                $"{SkyPropertyName} is not a static bool property, patch disabled");
            return;
        }

        _canSkyBeActive = canSkyBeActive.CreateDelegate<Func<bool>>();
        MonoModHooks.Modify(skyActive, AnswerFromCachedGetter);
    }

    // Short circuits the getter rather than replacing its body, so what is left behind is the
    // mod's own dead code rather than this patch's idea of what the method used to say.
    private void AnswerFromCachedGetter(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);

        // A cached handle is only the same answer while the getter is still resolving this exact
        // property. Both names have to appear, in the order the expression pushes them.
        if (!cursor.TryGotoNext(i => i.MatchLdstr(SkyTypeName))
            || !cursor.TryGotoNext(i => i.MatchLdstr(SkyPropertyName)))
        {
            Mod.Logger.Error($"Exo mech sky lookup: SkyActive no longer resolves {SkyTypeName}." +
                $"{SkyPropertyName} by name, patch disabled");
            return;
        }

        cursor.Index = 0;
        cursor.EmitDelegate<Func<bool>>(ReadSky);
        cursor.Emit(OpCodes.Ret);
    }

    private static bool ReadSky() => _canSkyBeActive();
}
