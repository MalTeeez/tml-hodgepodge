using System.Collections.Generic;
using System.Reflection;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// TimeFrozenPrevention.PreAI runs for every NPC every tick and is a chain of tests, each of which
// compares npc.type against an id it rebuilds from a name:
//
//     if (InfernalCrossmod.Catalyst.Loaded && npc.type == InfernalCrossmod.Catalyst.Mod.Find<ModNPC>("Astrageldon").Type)
//     if (npc.type == ModContent.NPCType<DevourerofGodsHead>())
//     if (ModLoader.TryGetMod("CalamityHunt", ref val) && npc.type == val.Find<ModNPC>("Goozma").Type)
//
// Seven of them, plus a HashSet lookup and a read of ModNPC.Name put through four string.Contains
// calls. Every id is fixed once mods finish loading. In the blood moon capture the method costs
// 1.30% of the thread and it is the third largest caller of the dictionary churn behind
// InfernalItemBalanceChange.
//
// Both spellings fold to the number they return, which is what the mod would have compiled to if
// the ids were constants. Nothing else about the method changes, so every test still asks what it
// asked before and the Loaded guards still gate their own branches.
//
// The Name read is left alone. It is cheap once ModTypeNameCache is on, and folding a string
// comparison would mean deciding which mod owns an NPC at patch time.
//
// Read against InfernalEclipseAPI 0.10.8.
public class TimeFrozenIdLookup : Patch
{
    private const string TargetMod = "InfernalEclipseAPI";
    private const string TargetType =
        "InfernalEclipseAPI.Common.Globals.GlobalNPCs.NPCDebuffs.TimeFrozenPrevention";

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().TimeFrozenIdLookup;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod infernalEclipse))
            return;

        // The type carries ExtendsFromMod("SOTS"), so it only exists when SOTS is loaded.
        MethodInfo preAi = infernalEclipse.Code.GetType(TargetType)?.GetMethod("PreAI",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, binder: null,
            [typeof(NPC)], modifiers: null);

        if (preAi == null)
            return;

        MonoModHooks.Modify(preAi, il =>
        {
            List<string> skipped = [];
            int folded = ContentIdFolding.FoldContentLookups(il,
                ContentIdFolding.InAssembly(infernalEclipse.Code), skipped.Add);
            folded += ContentIdFolding.FoldNamedFinds(il, skipped.Add);

            ContentIdFolding.Report(Mod, "Time frozen id lookup", "PreAI", folded, skipped);
        });
    }
}
