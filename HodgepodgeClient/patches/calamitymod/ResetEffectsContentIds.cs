using System.Collections.Generic;
using System.Reflection;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// CalamityGlobalNPC.ResetEffects is a GlobalNPC hook, so it runs for every NPC every tick, and it
// resolves a run of ModContent lookups while clearing per NPC state. In the blood moon capture the
// method costs 1.37% of the thread, and 0.47% of that is inside ModContent.NPCType alone -- about
// a third of it spent asking for numbers that were handed out when content registered.
//
// Every lookup folds to the id it returns. Calamity's own resetting is untouched.
//
// Read against CalamityMod 2.2.4.
public class ResetEffectsContentIds : Patch
{
    private const string TargetMod = "CalamityMod";
    private const string TargetType = "CalamityMod.NPCs.CalamityGlobalNPC";

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().ResetEffectsContentIds;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod calamity))
            return;

        MethodInfo resetEffects = calamity.Code.GetType(TargetType)?.GetMethod("ResetEffects",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, binder: null,
            [typeof(NPC)], modifiers: null);

        if (resetEffects == null)
        {
            Mod.Logger.Error($"Reset effects content ids: {TargetType}.ResetEffects(NPC) is " +
                "missing, patch disabled");
            return;
        }

        MonoModHooks.Modify(resetEffects, il =>
        {
            List<string> skipped = [];
            int folded = ContentIdFolding.FoldContentLookups(il,
                ContentIdFolding.InAssembly(calamity.Code), skipped.Add);

            ContentIdFolding.Report(Mod, "Reset effects content ids", "ResetEffects", folded,
                skipped);
        });
    }
}
