using System;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria.ModLoader;

namespace HodgepodgeServer;

// Base for a patch on a method whose whole body is unobservable without a screen. Patch only
// applies it on a dedicated server, so the injected return needs no runtime test and a client
// running this mod is left untouched.
public abstract class HeadlessNoOp : Patch
{
    protected abstract string TargetMod { get; }

    protected abstract string TargetType { get; }

    protected abstract string TargetMethod { get; }

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod target))
            return;

        // Declared on the target itself, taking nothing and returning nothing. Without those the
        // lookup can bind an inherited override, or throw once an overload appears; and the
        // injected `ret` is only valid for a void method.
        MethodInfo method = target.Code.GetType(TargetType)?.GetMethod(TargetMethod,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly,
            binder: null, Type.EmptyTypes, modifiers: null);

        if (method == null || method.ReturnType != typeof(void))
        {
            Mod.Logger.Error($"{GetType().Name}: {TargetType}.{TargetMethod} is not a declared " +
                "parameterless void method, patch disabled");
            return;
        }

        MonoModHooks.Modify(method, il => new ILCursor(il).Emit(OpCodes.Ret));
    }
}
