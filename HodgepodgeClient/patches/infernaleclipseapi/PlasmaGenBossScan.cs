using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// PlasmaGenBlocker.UpdateAccessory is a GlobalItem hook with no AppliesToEntity, so it runs once
// per equipped accessory per player per tick, and it never reads the item it was given. Each run
// scans Main.npc once for every id in a static set of thirteen Calamity bosses, then resolves
// three mods by name, four NPCs by name, and scans for each of those:
//
//     foreach (int boss in BlockedBosses)
//         if (NPC.AnyNPCs(boss))
//             player.GetThoriumPlayer().accPlasmaGenerator = false;
//     if (ModLoader.TryGetMod("CatalystMod", out var result) && NPC.AnyNPCs(result.Find<ModNPC>("Astrageldon").Type))
//         player.GetThoriumPlayer().accPlasmaGenerator = false;
//
// Seventeen world scans to answer one yes-or-no question whose only effect is clearing one bool on
// the player. On the lower-end client that was about 0.05 ms/frame in every scene.
//
// The ids are fixed once mods have loaded, so they are gathered once, the thirteen straight out of
// the mod's own set and the four by the names its IL hands to Find; the question is the same for
// every accessory on every player in a tick, so it is answered once per tick from a single pass
// over the active NPCs. When the answer is yes the bool is cleared exactly as before, through the
// same call and the same field the original used. A named boss added or removed upstream disables
// the patch rather than going untested.
//
// A boss spawned by a player's item use is seen one tick later than the original would see it,
// because accessories update before items are used. Nothing in a boss fight turns on that tick.
//
// Read against InfernalEclipseAPI 0.10.8.
public class PlasmaGenBossScan : Patch
{
    private const string TargetMod = "InfernalEclipseAPI";
    private const string TargetType = "InfernalEclipseAPI.Common.Globals.GlobalItems.ItemReworks." +
        "Accessories.PlasmaGenBlocker";
    private const string BlockedField = "BlockedBosses";

    private static readonly (string Mod, string Npc)[] NamedBosses =
    [
        ("CatalystMod", "Astrageldon"), ("CalamityHunt", "Goozma"),
        ("NoxusBoss", "NamelessDeityBoss"), ("NoxusBoss", "AvatarOfEmptiness"),
    ];

    // Indexed by NPC type. A boss from a mod that is not loaded has no entry, which is the same
    // answer the original reaches through its TryGetMod test.
    private static bool[] _blocking;
    private static uint _scannedTick = uint.MaxValue;
    private static bool _anyAlive;

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().PlasmaGenBossScan;

    public static bool AnyBlockingBossAlive()
    {
        if (_scannedTick == Main.GameUpdateCount)
            return _anyAlive;

        _anyAlive = false;
        foreach (NPC npc in Main.ActiveNPCs)
        {
            if (_blocking[npc.type])
            {
                _anyAlive = true;
                break;
            }
        }

        _scannedTick = Main.GameUpdateCount;
        return _anyAlive;
    }

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod infernalEclipse))
            return;

        // The type carries ExtendsFromMod("ThoriumMod"), so it only exists when Thorium is loaded.
        Type blocker = infernalEclipse.Code.GetType(TargetType);
        if (blocker == null)
            return;

        MethodInfo updateAccessory = blocker.GetMethod("UpdateAccessory",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, binder: null,
            [typeof(Item), typeof(Player), typeof(bool)], modifiers: null);
        FieldInfo blocked = blocker.GetField(BlockedField,
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        if (updateAccessory == null || blocked?.FieldType != typeof(HashSet<int>)
            || !ReadsField(updateAccessory, blocked))
        {
            Mod.Logger.Error($"Plasma gen boss scan: {TargetType}.UpdateAccessory no longer " +
                $"scans for the ids in a static HashSet<int> {BlockedField}, patch disabled");
            return;
        }

        _blocking = new bool[NPCLoader.NPCCount];
        foreach (int type in (HashSet<int>)blocked.GetValue(null))
        {
            if (type >= 0 && type < _blocking.Length)
                _blocking[type] = true;
        }

        foreach ((string modName, string npcName) in NamedBosses)
        {
            if (!ModLoader.TryGetMod(modName, out Mod owner))
                continue;

            if (!owner.TryFind(npcName, out ModNPC boss))
            {
                Mod.Logger.Error($"Plasma gen boss scan: {modName} has no NPC named {npcName}, " +
                    "patch disabled");
                return;
            }

            _blocking[boss.Type] = true;
        }

        MonoModHooks.Modify(updateAccessory, ReplaceWithCachedScan);
    }

    private void ReplaceWithCachedScan(ILContext il)
    {
        HashSet<string> named = NamesFound(il);
        if (!named.SetEquals(NamedBosses.Select(boss => boss.Npc)))
        {
            Mod.Logger.Error("Plasma gen boss scan: UpdateAccessory finds a different set of " +
                $"bosses by name ({string.Join(", ", named)}), patch disabled");
            return;
        }

        // The clearing is re-emitted from the original's own instructions rather than by name, so
        // the Thorium side of it never has to be resolved here.
        Instruction thoriumPlayer = il.Body.Instructions.FirstOrDefault(instruction =>
            instruction.MatchCall(out MethodReference called)
            && called.Name == "GetThoriumPlayer" && called.Parameters.Count == 1
            && instruction.Previous?.OpCode == OpCodes.Ldarg_2);
        Instruction clear = il.Body.Instructions.FirstOrDefault(instruction =>
            instruction.MatchStfld(out FieldReference field)
            && field.Name == "accPlasmaGenerator"
            && instruction.Previous?.OpCode == OpCodes.Ldc_I4_0);

        if (thoriumPlayer == null || clear == null)
        {
            Mod.Logger.Error("Plasma gen boss scan: UpdateAccessory no longer clears " +
                "accPlasmaGenerator on the player's Thorium player, patch disabled");
            return;
        }

        ILCursor cursor = new ILCursor(il);
        ILLabel done = cursor.DefineLabel();
        cursor.Emit(OpCodes.Call,
            typeof(PlasmaGenBossScan).GetMethod(nameof(AnyBlockingBossAlive)));
        cursor.Emit(OpCodes.Brfalse, done);
        cursor.Emit(OpCodes.Ldarg_2);
        cursor.Emit(OpCodes.Call, (MethodReference)thoriumPlayer.Operand);
        cursor.Emit(OpCodes.Ldc_I4_0);
        cursor.Emit(OpCodes.Stfld, (FieldReference)clear.Operand);
        cursor.MarkLabel(done);
        cursor.Emit(OpCodes.Ret);
    }

    // The strings the method hands to Find<ModNPC>.
    private static HashSet<string> NamesFound(ILContext il)
    {
        HashSet<string> names = [];
        foreach (Instruction instruction in il.Body.Instructions)
        {
            if (instruction.MatchCallvirt(out MethodReference called)
                && called is GenericInstanceMethod find
                && find.ElementMethod.Name == nameof(Mod.Find)
                && find.ElementMethod.DeclaringType.Name == nameof(Mod)
                && instruction.Previous?.OpCode == OpCodes.Ldstr)
                names.Add((string)instruction.Previous.Operand);
        }

        return names;
    }
}
