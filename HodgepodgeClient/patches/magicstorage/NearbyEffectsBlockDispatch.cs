using System;
using System.Collections.Generic;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// MagicStorage and SerousCommonLib both detour TileLoader.NearbyEffects, with the same body:
//
//     if (!DoBlockHooks)
//         orig(i, j, type, closer);
//
// MagicStorage depends on SerousCommonLib, so this looks like a copy left behind when the logic
// moved into the library -- but both flags are still written, so both hooks are live and neither
// can simply be dropped. NearbyEffects runs per tile in the player's vicinity every tick, which
// makes the pair 0.88% of the client thread spent reaching two static bools.
//
// Both tests move into NearbyEffects itself, so the tiles that are not being scanned pay two field
// reads instead of two trampolines. The flags are read as fields from the injected IL rather than
// through a delegate, because this runs per tile and the indirection is the thing being removed.
public class NearbyEffectsBlockDispatch : Patch
{
    // Mod, the type declaring the Hook field, and the type declaring the flag it tests. The two
    // are the same class in MagicStorage and separate in SerousCommonLib.
    private static readonly (string Mod, string HookType, string FlagType)[] Blockers =
    [
        ("MagicStorage", "MagicStorage.Edits.NearbyEffectsBlockingDuringPylonScanningDetour",
            "MagicStorage.Edits.NearbyEffectsBlockingDuringPylonScanningDetour"),
        ("SerousCommonLib", "SerousCommonLib.API.Edits.NearbyEffectsBlocking",
            "SerousCommonLib.API.Helpers.TileScanning"),
    ];

    private const string FlagName = "DoBlockHooks";
    private const string HookFieldName = "On_TileLoader_NearbyEffects";

    private static readonly List<FieldInfo> Flags = [];
    private static bool _injected;

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().NearbyEffectsBlockDispatch;

    protected override void Apply()
    {
        MethodInfo nearbyEffects = typeof(TileLoader).GetMethod("NearbyEffects",
            BindingFlags.Static | BindingFlags.Public);
        if (nearbyEffects == null)
        {
            Mod.Logger.Error("Nearby effects block dispatch: TileLoader.NearbyEffects is missing, " +
                "patch disabled");
            return;
        }

        // Statics, so a reload would otherwise stack the previous load's flags onto this one's.
        Flags.Clear();
        _injected = false;

        List<Hook> hooks = [];
        foreach ((string modName, string hookTypeName, string flagTypeName) in Blockers)
        {
            if (!ModLoader.TryGetMod(modName, out Mod blocker))
                continue;

            FieldInfo flag = blocker.Code.GetType(flagTypeName)?.GetField(FlagName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo hookField = blocker.Code.GetType(hookTypeName)?.GetField(HookFieldName,
                BindingFlags.Static | BindingFlags.NonPublic);

            if (flag?.FieldType != typeof(bool) || hookField?.GetValue(null) is not Hook hook)
            {
                Mod.Logger.Error($"Nearby effects block dispatch: {modName} no longer exposes a " +
                    $"static bool {FlagName} and an installed {HookFieldName}, patch disabled");
                return;
            }

            // The hook has to be the one on NearbyEffects, and its body has to be the bare flag
            // test this patch is replicating rather than something that also does work.
            if (hook.Source != nearbyEffects || !ReadsField(hook.Target, flag))
            {
                Mod.Logger.Error($"Nearby effects block dispatch: {modName}'s hook is no longer a " +
                    $"bare {FlagName} test on NearbyEffects, patch disabled");
                return;
            }

            Flags.Add(flag);
            hooks.Add(hook);
        }

        if (Flags.Count == 0)
            return;

        MonoModHooks.Modify(nearbyEffects, SkipWhileBlocked);

        // Only now is the test in both places, which is the safe state to remove one from.
        if (!_injected)
        {
            Mod.Logger.Error("Nearby effects block dispatch: the flag test did not inject into " +
                "NearbyEffects, the original hooks are left in place");
            return;
        }

        foreach (Hook hook in hooks)
        {
            hook.Undo();
            if (hook.IsApplied)
                Mod.Logger.Error($"Nearby effects block dispatch: {hook.Target.DeclaringType?.Name}" +
                    " would not undo, so its flag is now tested twice rather than once");
        }
    }

    // if (flagA || flagB) return; -- the same short circuit both detours performed, one call inward.
    private void SkipWhileBlocked(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);
        ILLabel blocked = cursor.DefineLabel();
        ILLabel run = cursor.DefineLabel();

        foreach (FieldInfo flag in Flags)
        {
            cursor.Emit(OpCodes.Ldsfld, flag);
            cursor.Emit(OpCodes.Brtrue, blocked);
        }

        cursor.Emit(OpCodes.Br, run);
        cursor.MarkLabel(blocked);
        cursor.Emit(OpCodes.Ret);
        cursor.MarkLabel(run);
        _injected = true;
    }
}
