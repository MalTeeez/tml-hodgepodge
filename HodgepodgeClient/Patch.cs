using System;
using System.Reflection;
using MonoMod.RuntimeDetour;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// Every patch resolves another mod's members by name and rewrites its IL, so every patch can fail
// on a dependency update. Failing has to cost this patch alone: an escaping exception here takes
// the game down during mod loading, which is worse than any patch is worth.
public abstract class Patch : ModSystem
{
    // Both are fixed width -- one opcode byte and a four byte field token -- which is what makes
    // the naive scan in ReadsField sound.
    private const byte Ldsfld = 0x7E;
    private const byte Ldfld = 0x7B;

    protected abstract bool Enabled { get; }

    protected abstract void Apply();

    public override void PostSetupContent()
    {
        if (!Enabled)
            return;

        try
        {
            Apply();
        }
        catch (Exception exception)
        {
            Mod.Logger.Error($"{GetType().Name}: disabled, {exception}");
        }
    }

    // Walks a method's IL for a load of a given field, static or instance. A patch that replaces a
    // method with a cheaper equivalent is only equivalent while the original still consults the
    // state the argument rests on, and this is how that gets pinned.
    protected static bool ReadsField(MethodBase method, FieldInfo field)
    {
        byte[] body = method.GetMethodBody()?.GetILAsByteArray();
        if (body == null)
            return false;

        for (int offset = 0; offset + 5 <= body.Length; offset++)
        {
            if (body[offset] != Ldsfld && body[offset] != Ldfld)
                continue;

            try
            {
                if (method.Module.ResolveField(BitConverter.ToInt32(body, offset + 1)) == field)
                    return true;
            }
            catch (ArgumentException)
            {
                // Not a field token: this byte was operand data, not an opcode. Keep scanning.
            }
        }

        return false;
    }

    // Removes a hook another mod registered through an On_ event, by rebuilding the delegate it
    // registered: delegates compare by target and method, and HookEndpointManager keys its table
    // on (method, delegate). It drops a miss silently, so the detour count is the only way to know
    // the removal landed -- and a patch that replaced a hook without removing it is not faster.
    protected bool Detach<THook>(MethodBase patched, object target, MethodInfo detour,
        Action<THook> remove) where THook : Delegate
    {
        int before = DetourCount(patched);
        remove((THook)Delegate.CreateDelegate(typeof(THook), target, detour));

        if (DetourCount(patched) < before)
            return true;

        Mod.Logger.Error($"{GetType().Name}: {detour.Name} did not detach from {patched.Name}, " +
            "so its work now happens twice rather than once");
        return false;
    }

    protected static int DetourCount(MethodBase method)
    {
        int count = 0;
        foreach (DetourInfo _ in DetourManager.GetDetourInfo(method).Detours)
            count++;

        return count;
    }
}
