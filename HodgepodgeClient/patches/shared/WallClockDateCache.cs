using System;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using MonoMod.Cil;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// Three mods read DateTime.Now from a per tick hook to answer a calendar question -- is it
// Christmas, is it April Fools -- and DateTime.Now is a timezone conversion rather than a clock
// read. Together they are 1.19% of the client thread.
//
// The three have to be redirected together. The first DateTime.Now of a tick pays a cold cost and
// later ones do not: Thorium's runs first in the tick at 0.117 ms, and CalValEX's, an identical
// single call, runs later at 0.045 ms. Patching one site alone largely hands its cost to whichever
// caller now goes first.
//
// A calendar day cannot change inside a tick, so answering all of them from a value refreshed once
// a second is exactly equivalent. This is the same defect AprilFoolsDateCheck removes from a
// different CalValEX method; that patch avoids the read entirely, this one makes it cheap.
public class WallClockDateCache : Patch
{
    private const int RefreshMilliseconds = 1000;

    // The methods whose DateTime.Now reads get redirected. A compiler generated lambda is matched
    // by the orig delegate it takes rather than by name, because its b__N_M ordinal shifts
    // whenever the owning mod's source moves.
    private static readonly (string Mod, string Type, string Method, string OrigParameter)[] Targets =
    [
        ("ThoriumMod", "ThoriumMod.ThoriumMiscSystem", "PostUpdateTime", null),
        ("CalValEX", "CalValEX.CalValEXWorld", "PreUpdateNPCs", null),
        ("RagnarokMod", "RagnarokMod.ILEditing.MiscEdits+<>c", null, "orig_PlaySound"),
    ];

    private static DateTime _date;
    private static long _refreshedAt = long.MinValue;

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().WallClockDateCache;

    // Environment.TickCount64 reads a counter the OS keeps updated, with none of the timezone work
    // that makes DateTime.Now worth caching in the first place. Called only from the main thread,
    // which is where every redirected caller runs.
    public static DateTime Now()
    {
        long now = Environment.TickCount64;
        if (now - _refreshedAt >= RefreshMilliseconds)
        {
            _date = DateTime.Now;
            _refreshedAt = now;
        }

        return _date;
    }

    protected override void Apply()
    {
        foreach ((string modName, string typeName, string methodName, string origParameter) in Targets)
        {
            if (!ModLoader.TryGetMod(modName, out Mod mod))
                continue;

            MethodInfo target = Resolve(mod.Code.GetType(typeName), methodName, origParameter);
            if (target == null)
            {
                Mod.Logger.Error($"Wall clock date cache: no {methodName ?? origParameter} on " +
                    $"{typeName}, {modName} left as it is");
                continue;
            }

            MonoModHooks.Modify(target, RedirectDateReads);
        }
    }

    private static MethodInfo Resolve(Type owner, string methodName, string origParameter)
    {
        MethodInfo[] declared = owner?.GetMethods(BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        if (declared == null)
            return null;

        return methodName != null
            ? declared.FirstOrDefault(method => method.Name == methodName)
            : declared.FirstOrDefault(method => method.GetParameters() is [var first, ..]
                && first.ParameterType.Name.StartsWith(origParameter, StringComparison.Ordinal));
    }

    // Rewrites the call target in place, so the arguments, the branches and everything the method
    // does with the result stay exactly as the mod wrote them. Both getters are static and return
    // a DateTime, so the substitution leaves the stack unchanged.
    private void RedirectDateReads(ILContext il)
    {
        MethodReference cachedNow = il.Import(typeof(WallClockDateCache).GetMethod(nameof(Now)));
        ILCursor cursor = new ILCursor(il);
        int redirected = 0;

        while (cursor.TryGotoNext(MoveType.After, i => i.MatchCall(out MethodReference called)
                && called.Name == "get_Now" && called.DeclaringType.FullName == "System.DateTime"))
        {
            cursor.Prev.Operand = cachedNow;
            redirected++;
        }

        if (redirected == 0)
            Mod.Logger.Error($"Wall clock date cache: {il.Method.Name} no longer reads " +
                "DateTime.Now, left as it is");
    }
}
