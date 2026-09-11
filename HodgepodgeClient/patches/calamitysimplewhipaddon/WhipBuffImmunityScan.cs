using System.Collections.Generic;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// WhipGripGlobalNPC.PostAI clears buff immunity for the whip addon's own debuffs, and finds them by
// walking every buff type in the game once for every NPC every tick:
//
//     for (int i = 0; i < BuffLoader.BuffCount; i++)
//         if (BuffLoader.GetBuff(i)?.Mod == ModContent.GetInstance<CalamitySimpleWhipAddon>())
//             npc.buffImmune[i] = false;
//
// BuffCount is in the thousands with a pack this size, the mod instance is fetched again on every
// one of those iterations, and the answer is the same seven ids it was last tick. On the profiled
// client the loop was 0.260 ms/frame, 0.95% of the thread, effectively all of it the mod's own
// inlined body rather than anything it calls.
//
// Which buffs belong to the mod is fixed once loading finishes, so the ids are collected once by
// the same test the loop uses and the immunity is cleared straight off that list. The mod's own
// buffs stay landable on exactly the NPCs they landed on before.
public class WhipBuffImmunityScan : Patch
{
    private const string TargetMod = "CalamitySimpleWhipAddon";
    private const string TargetType =
        "CalamitySimpleWhipAddon.Content.Common.GlobalNPCs.WhipGripGlobalNPC";

    private static int[] _buffs = [];

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().WhipBuffImmunityScan;

    public static void ClearOwnBuffImmunity(NPC npc)
    {
        foreach (int buff in _buffs)
            npc.buffImmune[buff] = false;
    }

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod whips))
            return;

        MethodInfo postAi = whips.Code.GetType(TargetType)?.GetMethod("PostAI",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, binder: null,
            [typeof(NPC)], modifiers: null);

        // The replacement writes buffImmune and nothing else, so a PostAI that has grown a second
        // job, or stopped touching immunity at all, is one this patch would quietly throw away.
        if (postAi?.ReturnType != typeof(void)
            || !ReadsField(postAi, typeof(NPC).GetField(nameof(NPC.buffImmune))))
        {
            Mod.Logger.Error($"Whip buff immunity scan: {TargetType}.PostAI(NPC) is not a void " +
                "method clearing buff immunity, patch disabled");
            return;
        }

        _buffs = OwnBuffs(whips);
        MonoModHooks.Modify(postAi, il =>
        {
            ILCursor cursor = new ILCursor(il);
            cursor.Emit(OpCodes.Ldarg_1);
            cursor.Emit(OpCodes.Call,
                typeof(WhipBuffImmunityScan).GetMethod(nameof(ClearOwnBuffImmunity)));
            cursor.Emit(OpCodes.Ret);
        });
    }

    // The loop's own test, run once. PostSetupContent is past the last buff registration, so the
    // list this produces is the list the loop would have produced on any later tick.
    private static int[] OwnBuffs(Mod whips)
    {
        List<int> buffs = [];
        for (int buff = 0; buff < BuffLoader.BuffCount; buff++)
        {
            if (BuffLoader.GetBuff(buff)?.Mod == whips)
                buffs.Add(buff);
        }

        return [.. buffs];
    }
}
