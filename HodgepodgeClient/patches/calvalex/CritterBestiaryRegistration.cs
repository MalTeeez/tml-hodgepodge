using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.GameContent.Bestiary;
using Terraria.ID;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// CVUtils.CritterBestiary is how CalValEX fills in a critter's bestiary entry on sight rather than
// on kill. Every one of its critters calls it from AI, once per tick, and once a player stands
// within bestiary range it does this, every tick, for as long as they stand there:
//
//     NPC nPC = new NPC();
//     nPC.SetDefaults(NPCType);
//     Main.BestiaryTracker.Kills.RegisterKill(nPC);
//
// SetDefaults runs the critter's own setup and every GlobalNPC.SetDefaults in the pack to produce
// an NPC whose only use is carrying a type into RegisterKill, and RegisterKill bumps the entry's
// kill count again for an entry that filled in on the first tick. 0.66% of the lower-end client's
// thread while mining, 0.129 ms per update.
//
// The sample NPC in ContentSamples carries the same defaults and is what RegisterKill reads, so
// it stands in for the throwaway. The kill is registered only while the entry is still short of
// fully unlocked: a critter unlocks in full on its first registered kill, so nothing the bestiary
// shows is reached later than before. What does change is the entry's kill counter, which stops
// climbing at sixty per second of standing beside the critter.
//
// Read against CalValEX 11.4.2.
public class CritterBestiaryRegistration : Patch
{
    private const string TargetMod = "CalValEX";
    private const string TargetType = "CalValEX.CVUtils";

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().CritterBestiaryRegistration;

    public static void RegisterNearbyCritter(NPC npc, int npcType)
    {
        for (int slot = 0; slot < Main.maxPlayers; slot++)
        {
            Player player = Main.player[slot];
            if (player == null || !player.active
                || !npc.Hitbox.Intersects(player.HitboxForBestiaryNearbyCheck))
                continue;

            if (!FullyUnlocked(npcType))
                Main.BestiaryTracker.Kills.RegisterKill(ContentSamples.NpcsByNetId[npcType]);

            return;
        }
    }

    private static bool FullyUnlocked(int npcType) =>
        Main.BestiaryDB.FindEntryByNPCID(npcType)?.UIInfoProvider?.GetEntryUICollectionInfo()
            .UnlockState == BestiaryEntryUnlockState.CanShowDropsWithDropRates_4;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod calValEx))
            return;

        MethodInfo critterBestiary = calValEx.Code.GetType(TargetType)?.GetMethod("CritterBestiary",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly, binder: null,
            [typeof(NPC), typeof(int)], modifiers: null);

        if (critterBestiary?.ReturnType != typeof(void))
        {
            Mod.Logger.Error($"Critter bestiary registration: {TargetType}.CritterBestiary(NPC, " +
                "int) is missing, patch disabled");
            return;
        }

        MonoModHooks.Modify(critterBestiary, ReplaceWithSampleRegistration);
    }

    // The replacement stands in for a method that builds an NPC, registers a kill for it and tests
    // the bestiary hitbox. One that has stopped doing any of those is one this must not replace.
    private void ReplaceWithSampleRegistration(ILContext il)
    {
        bool buildsNpc = il.Body.Instructions.Any(instruction =>
            instruction.MatchCallvirt(out MethodReference called)
            && called.Name == nameof(NPC.SetDefaults) && called.DeclaringType.Name == nameof(NPC));
        bool registersKill = il.Body.Instructions.Any(instruction =>
            instruction.MatchCallvirt(out MethodReference called)
            && called.Name == nameof(NPCKillsTracker.RegisterKill));
        bool testsRange = il.Body.Instructions.Any(instruction =>
            instruction.MatchCallvirt(out MethodReference called)
            && called.Name == "get_" + nameof(Player.HitboxForBestiaryNearbyCheck));

        if (!buildsNpc || !registersKill || !testsRange)
        {
            Mod.Logger.Error("Critter bestiary registration: CritterBestiary no longer builds an " +
                "NPC to register a kill for within bestiary range, patch disabled");
            return;
        }

        ILCursor cursor = new ILCursor(il);
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Ldarg_1);
        cursor.Emit(OpCodes.Call,
            typeof(CritterBestiaryRegistration).GetMethod(nameof(RegisterNearbyCritter)));
        cursor.Emit(OpCodes.Ret);
    }
}
