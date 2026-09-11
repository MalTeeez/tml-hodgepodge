using System;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeServer;

// XykWings.checkActiveWings walks every active projectile in the world to count how many of the
// owner's wings are up, and AI() asks it four separate times per wing per tick. With six players
// wearing the wings that is four full projectile scans per wing per tick, 0.105 ms of a 9.02 ms
// tick on the profiled server.
//
// The four answers within one AI() call are always the same. checkActiveWings has exactly five
// call sites, four in AI() and one in PreDraw, and the only thing in the class that can change the
// projectile set is Projectile.Kill: twice before the first of the four, and once in a branch that
// returns before reaching the second. Everything between the four call sites is float arithmetic.
// So scanning once per AI() call and reusing the count is exactly equivalent, not merely close.
//
// The count is reset on entry to AI() rather than kept for a tick, which is what keeps it correct
// when the next wing belongs to a different player. PreDraw is the one caller outside that window,
// and a dedicated server never draws.
//
// Read against CalamityMod 2026.6. The equivalence argument is about the shape of AI(), which no
// IL check can pin, so a Calamity update that spawns or kills a projectile mid-AI would need this
// patch turned off.
public class XykWingCountScan : Patch
{
    private const string TargetMod = "CalamityMod";
    private const string TargetType = "CalamityMod.Projectiles.Typeless.XykWings";

    private static int _wingType;
    private static int _count;
    private static bool _scanned;

    protected override bool Enabled => ModContent.GetInstance<ServerConfig>().XykWingCountScan;

    public static void BeginUpdate() => _scanned = false;

    public static int ActiveWings(ModProjectile self)
    {
        Projectile projectile = self.Projectile;

        // The mod throws away the count it built whenever ai[1] is 5, so do not build one.
        if (projectile.ai[1] == 5f)
            return 7;

        if (_scanned)
            return _count;

        _count = 0;
        foreach (Projectile other in Main.ActiveProjectiles)
        {
            // The mod compares against Owner.whoAmI, which is the slot the owner sits in, and
            // that is the same number Projectile.owner already holds.
            if (other.type == _wingType && other.owner == projectile.owner && other.ai[1] == 0f)
                _count++;
        }

        _scanned = true;
        return _count;
    }

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod calamity))
            return;

        Type owner = calamity.Code.GetType(TargetType);
        MethodInfo count = Declared(owner, "checkActiveWings");
        MethodInfo update = Declared(owner, "AI");

        if (count?.ReturnType != typeof(int) || update?.ReturnType != typeof(void))
        {
            Mod.Logger.Error($"Xyk wing count scan: {TargetType} has no parameterless int " +
                "checkActiveWings and void AI, patch disabled");
            return;
        }

        _wingType = calamity.Find<ModProjectile>("XykWings").Type;

        // The reset does nothing on its own -- no one reads the flag it clears until the counter
        // is in place -- while a counter left without its reset would answer every later call
        // from the first scan it ever made. So the reset goes in first, and a failure between the
        // two leaves the mod's own method doing the counting.
        MonoModHooks.Modify(update, ResetOnEntry);
        MonoModHooks.Modify(count, ReplaceWithCachedCount);
    }

    private static MethodInfo Declared(Type owner, string name) => owner?.GetMethod(name,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly,
        binder: null, Type.EmptyTypes, modifiers: null);

    private static void ReplaceWithCachedCount(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Call, typeof(XykWingCountScan).GetMethod(nameof(ActiveWings)));
        cursor.Emit(OpCodes.Ret);
    }

    private static void ResetOnEntry(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);
        cursor.Emit(OpCodes.Call, typeof(XykWingCountScan).GetMethod(nameof(BeginUpdate)));
    }
}
