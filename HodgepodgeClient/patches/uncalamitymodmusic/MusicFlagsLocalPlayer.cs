using System.Linq;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// MusicFlags.PreUpdate is a ModPlayer hook, so it runs once per active player per tick, and it
// derives a hundred-odd static biome, layer and time-of-day flags -- every one of them from
// Main.player[Main.myPlayer], never from the player it was invoked for:
//
//     Player player = Main.player[Main.myPlayer];
//     OverworldLayer = player.ZoneOverworldHeight;
//     ...
//
// The same five mod lookups, dozens of NPC scans and tile counts, repeated once per other player
// in the world to overwrite the statics with the values they already hold. On the lower-end client
// in multiplayer that was 0.69% of the thread, of which everything past the first run is repeat.
//
// Returning early for every player but the local one leaves each flag with the value the local
// run gave it. The one thing that shifts is timing: a run for a later slot reads the local
// player's zones after that player's update this tick rather than before it, so a flag can now
// settle one tick later than it did when a remote player happened to sit in a higher slot. The
// server mod already stops this method outright for the server's own dummy slot.
//
// Read against UnCalamityModMusic 2.1.3.
public class MusicFlagsLocalPlayer : Patch
{
    private const string TargetMod = "UnCalamityModMusic";
    private const string TargetType = "UnCalamityModMusic.Common.MusicFlags";

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().MusicFlagsLocalPlayer;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod unCalamityMusic))
            return;

        MethodInfo preUpdate = unCalamityMusic.Code.GetType(TargetType)?.GetMethod("PreUpdate",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, binder: null,
            System.Type.EmptyTypes, modifiers: null);

        // Every run being a repeat rests on the method reading the local slot and nothing off its
        // own instance. The first is a field read the IL walk can find; the second is the absence
        // of any `this` load at all, which Cecil can count.
        if (preUpdate == null || !ReadsField(preUpdate, typeof(Main).GetField(nameof(Main.myPlayer))))
        {
            Mod.Logger.Error($"Music flags local player: {TargetType}.PreUpdate is missing or no " +
                "longer reads Main.myPlayer, patch disabled");
            return;
        }

        MonoModHooks.Modify(preUpdate, SkipRemotePlayers);
    }

    // `if (Player.whoAmI != Main.myPlayer) return;` in front of the body.
    private void SkipRemotePlayers(ILContext il)
    {
        if (il.Body.Instructions.Any(instruction => instruction.MatchLdarg(0)))
        {
            Mod.Logger.Error("Music flags local player: PreUpdate now reads its own player, so " +
                "its runs are no longer interchangeable, patch disabled");
            return;
        }

        ILCursor cursor = new ILCursor(il);
        ILLabel local = cursor.DefineLabel();
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Call, typeof(ModPlayer).GetProperty(nameof(ModPlayer.Player)).GetMethod);
        cursor.Emit(OpCodes.Ldfld, typeof(Entity).GetField(nameof(Entity.whoAmI)));
        cursor.Emit(OpCodes.Ldsfld, typeof(Main).GetField(nameof(Main.myPlayer)));
        cursor.Emit(OpCodes.Beq, local);
        cursor.Emit(OpCodes.Ret);
        cursor.MarkLabel(local);
    }
}
