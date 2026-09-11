using System;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria.Graphics.Light;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// SOTS routes every LightingEngine.GetColor call -- one per tile per frame -- through a detour
// that does nothing but test one static bool. The test is worth keeping; the delegate dispatch and
// orig.Invoke trampoline around it are not. Inline the same test into GetColor itself and drop
// SOTS's hook, leaving one detour instead of a detour plus two delegate hops.
public class FullBrightDispatch : Patch
{
    private const string DetoursTypeName = "SOTS.SOTSDetours";
    private const string FakePlayerTypeName = "SOTS.FakePlayer.FakePlayerProjectile";
    private const string DetourMethodName = "LightingEngine_GetColor";

    // SOTS's detour byte for byte, with the three metadata tokens left open. Replicating a method
    // means owning it, so the whole body is pinned rather than its length: a same-length rewrite
    // -- a different return value, an extra branch, a fallback call -- has to fail this check, or
    // the patch removes SOTS's real hook and silently substitutes behaviour that no longer matches.
    private static readonly byte?[] ExpectedDetourBody =
    [
        0x7E, null, null, null, null,   // ldsfld FullBrightThisDrawCycle
        0x2C, 0x06,                     // brfalse.s over the early return
        0x28, null, null, null, null,   // call Vector3.get_One
        0x2A,                           // ret
        0x02, 0x03, 0x04, 0x05,         // ldarg.0 .. ldarg.3
        0x6F, null, null, null, null,   // callvirt orig.Invoke
        0x2A                            // ret
    ];

    private static FieldInfo _fullBrightField;
    private static bool _injected;

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().FullBrightDispatch;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod("SOTS", out Mod sots))
            return;

        _fullBrightField = sots.Code.GetType(FakePlayerTypeName)
            ?.GetField("FullBrightThisDrawCycle", BindingFlags.Static | BindingFlags.Public);
        Type detoursType = sots.Code.GetType(DetoursTypeName);
        MethodInfo sotsDetour = detoursType?.GetMethod(DetourMethodName,
            BindingFlags.Static | BindingFlags.NonPublic);

        if (_fullBrightField == null || sotsDetour == null)
        {
            Mod.Logger.Error("Full bright dispatch: FullBrightThisDrawCycle or " +
                $"{DetourMethodName} missing from SOTS, patch disabled");
            return;
        }

        if (!IsPlainFullBrightCheck(sotsDetour))
        {
            Mod.Logger.Error($"Full bright dispatch: SOTS.{DetourMethodName} is no longer a bare " +
                "fullbright check, patch disabled so SOTS keeps its own behaviour");
            return;
        }

        On_LightingEngine.hook_GetColor sotsHook = FindRegisteredHook(detoursType);
        if (sotsHook == null)
        {
            Mod.Logger.Error($"Full bright dispatch: SOTS's {DetourMethodName} hook instance not " +
                "found, patch disabled");
            return;
        }

        // Static, so without this the flag answers for the previous load, and SOTS's hook comes
        // off on the strength of an injection that did not happen this time.
        _injected = false;
        MonoModHooks.Modify(
            typeof(LightingEngine).GetMethod(nameof(LightingEngine.GetColor),
                new[] { typeof(int), typeof(int) }),
            InlineFullBrightCheck);

        // Only now is the check running in two places at once, which is the safe state to
        // remove one of them from. Reversing this order deletes the feature if the inject missed.
        if (_injected)
            On_LightingEngine.GetColor -= sotsHook;
    }

    private void InlineFullBrightCheck(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);
        ILLabel litNormally = cursor.DefineLabel();
        cursor.Emit(OpCodes.Ldsfld, _fullBrightField);
        cursor.Emit(OpCodes.Brfalse, litNormally);
        cursor.Emit(OpCodes.Call, typeof(Vector3).GetProperty(nameof(Vector3.One)).GetMethod);
        cursor.Emit(OpCodes.Ret);
        cursor.MarkLabel(litNormally);
        _injected = true;
    }

    // Opcodes have to match exactly and the three operands have to name the members being
    // replicated; a token pointing anywhere else means this is a different method that happens to
    // be the same shape.
    private static bool IsPlainFullBrightCheck(MethodInfo detour)
    {
        byte[] body = detour.GetMethodBody()?.GetILAsByteArray();
        if (body == null || body.Length != ExpectedDetourBody.Length)
            return false;

        for (int offset = 0; offset < body.Length; offset++)
        {
            if (ExpectedDetourBody[offset] is byte expected && body[offset] != expected)
                return false;
        }

        Module module = detour.Module;
        return module.ResolveField(BitConverter.ToInt32(body, 1)) == _fullBrightField
            && module.ResolveMethod(BitConverter.ToInt32(body, 8)) is MethodInfo one
            && one.DeclaringType == typeof(Vector3) && one.Name == "get_One"
            && module.ResolveMethod(BitConverter.ToInt32(body, 18)) is { Name: "Invoke" };
    }

    // The compiler caches the delegate SOTS registered in a nested <>O class. Match it by the
    // method it points at; the generated field's ordinal shifts whenever SOTS's source moves.
    private static On_LightingEngine.hook_GetColor FindRegisteredHook(Type detoursType) =>
        detoursType.GetNestedType("<>O", BindingFlags.NonPublic)
            ?.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(field => field.GetValue(null) as On_LightingEngine.hook_GetColor)
            .FirstOrDefault(hook => hook?.Method.Name == DetourMethodName);
}
