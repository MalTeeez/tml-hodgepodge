using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// LobotomizeAstrageldon.PreAI slows Catalyst's Astrageldon down while Goozma is alive. It runs for
// every NPC every tick, and the whole method is eight lines:
//
//     if (ModLoader.HasMod(HUtils.CatalystMod)) {
//         Mod mod = ModLoader.GetMod(HUtils.CatalystMod);
//         NPC.FindFirstNPC(mod.Find<ModNPC>("Astrageldon").Type);
//         if (NPC.AnyNPCs(ModContent.NPCType<Goozma>()) && npc.type == mod.Find<ModNPC>("Astrageldon").Type && npc.active) {
//             npc.velocity *= 0.98f;
//             return false;
//         }
//     }
//     return true;
//
// Three things are wrong with it at once. The FindFirstNPC call is a whole scan of Main.npc whose
// result is assigned to nothing. AnyNPCs is a second such scan, and it is evaluated in front of the
// npc.type test that is the only thing which can make its answer matter, so it runs for every NPC
// in the world rather than for the one Astrageldon. The mod handle and the NPC id behind it are
// rebuilt by name three times per call and never change after loading. Together, 0.113 ms/frame on
// the profiled client.
//
// The replacement asks the cheap question first, keeps the one scan whose answer is used, and
// resolves both ids once. An Astrageldon with Goozma alive is slowed exactly as before.
public class AstrageldonPresenceScan : Patch
{
    private const string TargetMod = "CalamityHunt";
    private const string TargetType = "CalamityHunt.Common.GlobalNPCs.LobotomizeAstrageldon";
    private const string CatalystMod = "CatalystMod";

    // -1 when Catalyst is absent, which no NPC's type can equal -- the same answer the original
    // reaches through its HasMod test, without the lookup.
    private static int _astrageldon = -1;
    private static int _goozma = -1;

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().AstrageldonPresenceScan;

    public static bool SlowAstrageldonNearGoozma(NPC npc)
    {
        if (npc.type != _astrageldon || !npc.active || !NPC.AnyNPCs(_goozma))
            return true;

        npc.velocity *= 0.98f;
        return false;
    }

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod hunt))
            return;

        MethodInfo preAi = hunt.Code.GetType(TargetType)?.GetMethod("PreAI",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, binder: null,
            [typeof(NPC)], modifiers: null);

        // The replacement does one thing to the NPC it is given, so a PreAI that has taken on
        // something besides the slowdown is one this patch must not stand in for.
        if (preAi?.ReturnType != typeof(bool)
            || !ReadsField(preAi, typeof(Entity).GetField(nameof(Entity.velocity))))
        {
            Mod.Logger.Error($"Astrageldon presence scan: {TargetType}.PreAI(NPC) is not a bool " +
                "method slowing the NPC down, patch disabled");
            return;
        }

        _goozma = hunt.Find<ModNPC>("Goozma").Type;
        _astrageldon = ModLoader.TryGetMod(CatalystMod, out Mod catalyst)
            ? catalyst.Find<ModNPC>("Astrageldon").Type : -1;

        MonoModHooks.Modify(preAi, il =>
        {
            ILCursor cursor = new ILCursor(il);
            cursor.Emit(OpCodes.Ldarg_1);
            cursor.Emit(OpCodes.Call,
                typeof(AstrageldonPresenceScan).GetMethod(nameof(SlowAstrageldonNearGoozma)));
            cursor.Emit(OpCodes.Ret);
        });
    }
}
