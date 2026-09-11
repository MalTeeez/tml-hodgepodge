using System;
using System.Linq;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// CalamityMod.WeakReferenceSupport.InAnySubworld walks every loaded mod and asks SubworldLibrary,
// through a string keyed mod call that boxes its argument, whether that mod has a subworld active:
//
//     foreach (Mod mod in ModLoader.Mods)
//         if (!mod.Name.Equals(subworldLibrary.Name)
//             && subworldLibrary.Call("AnyActive", mod) as bool? == true)
//             return true;
//
// With this pack that is 72 mod calls per invocation, and Infernum's abyss requirement hook calls
// it every tick. SubworldLibrary answers the same question from one field: AnyActive() is
// `current != null`, and AnyActive(mod) is `current?.Mod == mod`.
//
// The loop therefore differs from AnyActive() only when the active subworld belongs to
// SubworldLibrary itself, which the loop skips by name. SubworldLibrary ships no subworlds of its
// own, so there is nothing for that branch to find.
public class SubworldActiveScan : Patch
{
    private const string TargetMod = "CalamityMod";
    private const string TargetType = "CalamityMod.WeakReferenceSupport";
    private const string TargetMethod = "InAnySubworld";
    private const string SubworldMod = "SubworldLibrary";
    private const string SubworldSystemTypeName = "SubworldLibrary.SubworldSystem";

    private static Func<bool> _anySubworldActive;

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().SubworldActiveScan;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod calamity)
            || !ModLoader.TryGetMod(SubworldMod, out Mod subworldLibrary))
            return;

        MethodInfo inAnySubworld = calamity.Code.GetType(TargetType)?.GetMethod(TargetMethod,
            BindingFlags.Static | BindingFlags.NonPublic, binder: null, Type.EmptyTypes,
            modifiers: null);
        MethodInfo anyActive = subworldLibrary.Code.GetType(SubworldSystemTypeName)?.GetMethods(
            BindingFlags.Static | BindingFlags.Public).SingleOrDefault(method =>
                method.Name == "AnyActive" && !method.IsGenericMethod
                && method.GetParameters().Length == 0);

        if (inAnySubworld?.ReturnType != typeof(bool) || anyActive?.ReturnType != typeof(bool))
        {
            Mod.Logger.Error($"Subworld active scan: a parameterless bool {TargetType}." +
                $"{TargetMethod} or {SubworldSystemTypeName}.AnyActive is missing, patch disabled");
            return;
        }

        _anySubworldActive = anyActive.CreateDelegate<Func<bool>>();
        MonoModHooks.Modify(inAnySubworld, AnswerWithoutScanning);
    }

    // Short circuits the scan rather than replacing the body, leaving Calamity's own code behind
    // as dead code instead of this patch's idea of what it used to say.
    private void AnswerWithoutScanning(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);

        // Skipping the loop is only equivalent while the loop is still the mod call scan described
        // above. If Calamity asks SubworldLibrary something else, this stops matching.
        if (!cursor.TryGotoNext(i => i.MatchLdstr("AnyActive")))
        {
            Mod.Logger.Error($"Subworld active scan: {TargetMethod} no longer issues an " +
                "\"AnyActive\" mod call, patch disabled");
            return;
        }

        cursor.Index = 0;
        cursor.EmitDelegate<Func<bool>>(AnySubworldActive);
        cursor.Emit(OpCodes.Ret);
    }

    private static bool AnySubworldActive() => _anySubworldActive();
}
