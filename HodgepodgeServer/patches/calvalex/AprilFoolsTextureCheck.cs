using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeServer;

// CalPaintOverride is a GlobalNPC whose FindFrame opens with `if (!TexturesActive) return;`, so the
// property is evaluated for every NPC in the world every tick. It reads DateTime.Now up to five
// times -- each a timezone conversion rather than a clock read -- to work out whether it is April
// 1st, and only then reaches its last line, `return Main.netMode == 0;`.
//
// That final line makes the property false whenever netMode is not 0, and a dedicated server is
// always netMode 2, so returning false up front is exactly equivalent here: with the date
// condition true the original returns false, and with it false the original returns false.
//
// Animation frames decide which sprite row gets drawn and a dedicated server draws nothing, so
// this was the single largest piece of work on the profiled server -- 0.671 ms of a 9.02 ms tick,
// and 96% of everything NPCLoader.FindFrame cost there.
//
// The client mod carries the same patch against the same property. That one keeps the netMode test
// so April Fools still works in single player, which is the only place it can activate at all.
public class AprilFoolsTextureCheck : Patch
{
    private const string TargetMod = "CalValEX";
    private const string TargetType = "CalValEX.AprilFools.CalPaintOverride";
    private const string TargetProperty = "TexturesActive";

    protected override bool Enabled =>
        ModContent.GetInstance<ServerConfig>().AprilFoolsTextureCheck;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod calValEx))
            return;

        MethodInfo getter = calValEx.Code.GetType(TargetType)
            ?.GetProperty(TargetProperty, BindingFlags.Static | BindingFlags.Public)
            ?.GetMethod;

        if (getter == null || getter.ReturnType != typeof(bool))
        {
            Mod.Logger.Error($"April fools texture check: {TargetType}.{TargetProperty} is not a " +
                "static bool property, patch disabled");
            return;
        }

        // The equivalence argument above rests entirely on the getter still consulting netMode. If
        // CalValEX drops that term the property can be true on a server, and an unconditional
        // false would silently disable a working feature instead of skipping dead work.
        if (!ReadsField(getter, typeof(Main).GetField(nameof(Main.netMode))))
        {
            Mod.Logger.Error($"April fools texture check: {TargetProperty} no longer reads " +
                "Main.netMode, so returning false would change behaviour, patch disabled");
            return;
        }

        MonoModHooks.Modify(getter, ReturnFalse);
    }

    private static void ReturnFalse(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);
        cursor.Emit(OpCodes.Ldc_I4_0);
        cursor.Emit(OpCodes.Ret);
    }
}
