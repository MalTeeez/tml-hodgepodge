using System;
using System.Reflection;
using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// XykWings.checkActiveWings walks every active projectile in the world to count how many of the
// owner's wings are up, resolving ModContent.ProjectileType<XykWings>() inside the loop, and AI()
// asks it four separate times per wing per tick. PreDraw asks a fifth time per wing per frame and
// then requests its textures by path. The server mod already carries this fix for AI, where it was
// 0.105 ms/tick with six players; on the lower-end client the count was 0.42% of the thread and
// the texture requests another 0.15%.
//
// The four answers within one AI() call are always the same. checkActiveWings has exactly five
// call sites, four in AI() and one in PreDraw, and the only thing in the class that can change the
// projectile set is Projectile.Kill: twice before the first of the four, and once in a branch that
// returns before reaching the second. Everything between the four call sites is float arithmetic.
// So scanning once per AI() call and reusing the count is exactly equivalent, not merely close.
//
// Drawing is the other window. Nothing kills or spawns a projectile between the end of one update
// and the start of the next, so within a draw pass the count for an owner is fixed, and the wings
// of one owner are drawn one after another. The cache therefore remembers whose count it holds,
// and PreDraw starts a fresh count the first time it runs after an update, since a wing can die of
// timeLeft after the last AI() of the tick has counted it.
//
// Read against CalamityMod 2.2.4. The equivalence argument is about the shape of AI(), which no IL
// check can pin, so a Calamity update that spawns or kills a projectile mid-AI would need this
// patch turned off.
public class XykWingCountScan : Patch
{
    private const string TargetMod = "CalamityMod";
    private const string TargetType = "CalamityMod.Projectiles.Typeless.XykWings";

    private static int _wingType;
    private static int _count;
    private static int _owner;
    private static bool _scanned;
    private static bool _updated;

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().XykWingCountScan;

    public static void BeginUpdate()
    {
        _scanned = false;
        _updated = true;
    }

    public static void BeginDraw()
    {
        if (!_updated)
            return;

        _scanned = false;
        _updated = false;
    }

    public static int ActiveWings(ModProjectile self)
    {
        Projectile projectile = self.Projectile;

        // The mod throws away the count it built whenever ai[1] is 5, so do not build one.
        if (projectile.ai[1] == 5f)
            return 7;

        if (_scanned && _owner == projectile.owner)
            return _count;

        _count = 0;
        foreach (Projectile other in Main.ActiveProjectiles)
        {
            // The mod compares against Owner.whoAmI, which is the slot the owner sits in, and
            // that is the same number Projectile.owner already holds.
            if (other.type == _wingType && other.owner == projectile.owner && other.ai[1] == 0f)
                _count++;
        }

        _owner = projectile.owner;
        _scanned = true;
        return _count;
    }

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod calamity))
            return;

        Type owner = calamity.Code.GetType(TargetType);
        MethodInfo count = Declared(owner, "checkActiveWings", Type.EmptyTypes);
        MethodInfo update = Declared(owner, "AI", Type.EmptyTypes);
        MethodInfo preDraw = Declared(owner, "PreDraw", [typeof(Color).MakeByRefType()]);

        if (count?.ReturnType != typeof(int) || update?.ReturnType != typeof(void)
            || preDraw?.ReturnType != typeof(bool))
        {
            Mod.Logger.Error($"Xyk wing count scan: {TargetType} has no parameterless int " +
                "checkActiveWings, void AI and bool PreDraw(ref Color), patch disabled");
            return;
        }

        _wingType = calamity.Find<ModProjectile>("XykWings").Type;

        // The two resets do nothing on their own -- no one reads the flags they clear until the
        // counter is in place -- while a counter left without them would answer every later call
        // from the first scan it ever made. So the resets go in first, and a failure between them
        // and the counter leaves the mod's own method doing the counting.
        MonoModHooks.Modify(update, ResetOnEntry);
        MonoModHooks.Modify(preDraw, ResetOnDraw);
        MonoModHooks.Modify(count, ReplaceWithCachedCount);
    }

    private static MethodInfo Declared(Type owner, string name, Type[] parameters) =>
        owner?.GetMethod(name, BindingFlags.Instance | BindingFlags.Public
            | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, binder: null, parameters,
            modifiers: null);

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

    private void ResetOnDraw(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);
        cursor.Emit(OpCodes.Call, typeof(XykWingCountScan).GetMethod(nameof(BeginDraw)));

        if (TextureRequestCache.RewriteRequests(il) == 0)
            Mod.Logger.Error("Xyk wing count scan: PreDraw no longer requests its textures by " +
                "name, left as they are");
    }
}
