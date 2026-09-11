using System;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// DebuffNPC.PostDraw draws the stack counter for each of SOTS' nine permanent curses, and asks for
// the nine icon textures by name before it knows whether any of them will be drawn:
//
//     DrawPermanentDebuffs(npc, spriteBatch, screenPos, Color.White,
//         ModContent.Request<Texture2D>("SOTS/Common/GlobalNPCs/PlatinumCurse", ImmediateLoad).Value,
//         ref PlatinumCurse, ref Height);
//
// DrawPermanentDebuffs opens with `if (DebuffVariable > 0)` and does nothing otherwise, which is the
// case for every NPC carrying no SOTS curse -- so nine repository lookups run, each of them forcing
// the asset to load, to feed a method that discards the result. On the profiled client that was the
// whole of the method: 0.552 ms/frame, 2.03% of the thread, none of it in drawing.
//
// The counters are the same values DrawPermanentDebuffs tests, so a tick where all nine are zero is
// a tick where PostDraw has nothing to do. The guard reads them off the instance and returns before
// the first lookup; an NPC with any curse on it takes the method exactly as it was.
public class CurseIconDrawGate : Patch
{
    private const string TargetMod = "SOTS";
    private const string TargetType = "SOTS.Common.GlobalNPCs.DebuffNPC";

    // Named rather than discovered by type: DebuffNPC carries other ints, and a guard that skipped
    // the draw on a counter the method does not test would hide a curse instead of a lookup.
    private static readonly string[] CurseCounters =
    [
        "PlatinumCurse", "HarvestCurse", "DestableCurse", "BleedingCurse", "BlazingCurse",
        "AnomalyCurse", "BlightCurse", "CrystalCurse", "DamageCurse",
    ];

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().CurseIconDrawGate;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod sots))
            return;

        Type owner = sots.Code.GetType(TargetType);
        MethodInfo postDraw = owner?.GetMethod("PostDraw",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, binder: null,
            [typeof(NPC), typeof(SpriteBatch), typeof(Vector2), typeof(Color)], modifiers: null);

        if (postDraw == null)
        {
            Mod.Logger.Error($"Curse icon draw gate: {TargetType}.PostDraw is missing, " +
                "patch disabled");
            return;
        }

        FieldInfo[] counters = new FieldInfo[CurseCounters.Length];
        for (int curse = 0; curse < counters.Length; curse++)
        {
            counters[curse] = owner.GetField(CurseCounters[curse],
                BindingFlags.Instance | BindingFlags.Public);

            // A counter the method no longer passes to the draw is one this guard cannot speak
            // for, and a tenth curse added beside them would be drawn on a frame the guard skips.
            if (counters[curse]?.FieldType != typeof(int) || !ReadsField(postDraw, counters[curse]))
            {
                Mod.Logger.Error($"Curse icon draw gate: PostDraw no longer draws from " +
                    $"{CurseCounters[curse]}, patch disabled");
                return;
            }
        }

        MonoModHooks.Modify(postDraw, il => SkipWhenUncursed(il, counters));
    }

    // `if ((PlatinumCurse | HarvestCurse | ...) == 0) return;` in front of the original body. The
    // counters only ever climb from zero, and a negative one would leave the bits set and take the
    // original path, so the bitwise test errs towards drawing.
    private static void SkipWhenUncursed(ILContext il, FieldInfo[] counters)
    {
        ILCursor cursor = new ILCursor(il);
        ILLabel draw = cursor.DefineLabel();

        for (int curse = 0; curse < counters.Length; curse++)
        {
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.Emit(OpCodes.Ldfld, counters[curse]);
            if (curse > 0)
                cursor.Emit(OpCodes.Or);
        }

        cursor.Emit(OpCodes.Brtrue, draw);
        cursor.Emit(OpCodes.Ret);
        cursor.MarkLabel(draw);
    }
}
