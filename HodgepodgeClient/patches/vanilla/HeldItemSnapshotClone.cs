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

// Player.lastVisualizedSelectedItem is the clone of the held item that draw code reads as
// PlayerDrawSet.heldItem. Vanilla refreshes it in three places. Two of them clone every tick:
//
//     if (itemAnimation == 0) lastVisualizedSelectedItem = HeldItem.Clone();     // ItemCheckWrapped, every player
//     else lastVisualizedSelectedItem = item.Clone();                            // ItemCheck_Inner, remote players
//
// and the third, for the local player during item use, clones only when the item is not the one
// already held:
//
//     if ((itemTimeMax != 0 && itemTime == itemTimeMax) | (!item.IsAir && item.IsNotTheSameAs(lastVisualizedSelectedItem)))
//
// where IsNotTheSameAs compares netID, stack and prefix. Each clone runs the ModItem's Clone and
// every GlobalItem.Clone in the pack, which with this many mods made Item.Clone the largest single
// unclaimed cost in the idle capture: 1.60% of the thread, 0.29 ms/frame, and it grows with the
// player count.
//
// The two unconditional sites get vanilla's own test: the clone is kept while the held item still
// has the netID, stack and prefix it was cloned from. The local player's guarded site is left as
// it is, including its refresh at the start of every use.
//
// What this can change is per-instance state that a mod writes on the held item and reads back
// off the snapshot expecting this tick's value, since the key does not see it. tools/scan_helditem.py
// lists every method in the enabled mods that reads the snapshot; of those, only Thorium's held
// item layer reaches into a ModItem, and the array it reads is cloned by reference and updated in
// place, so the snapshot sees the same values either way.
public class HeldItemSnapshotClone : Patch
{
    private static readonly FieldInfo Snapshot = typeof(Player).GetField(
        nameof(Player.lastVisualizedSelectedItem));

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().HeldItemSnapshotClone;

    // Stands in for `held.Clone()` with the current snapshot alongside. A snapshot that is the held
    // item itself -- SOTS aliases the two while it hijacks drawing -- is replaced as vanilla would.
    public static Item ReuseOrClone(Item held, Item snapshot) =>
        snapshot != null && !ReferenceEquals(held, snapshot) && held.type == snapshot.type
            && held.netID == snapshot.netID && held.stack == snapshot.stack
            && held.prefix == snapshot.prefix
            ? snapshot : held.Clone();

    protected override void Apply()
    {
        MethodInfo wrapped = typeof(Player).GetMethod("ItemCheckWrapped",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            binder: null, [typeof(int)], modifiers: null);
        MethodInfo inner = typeof(Player).GetMethod("ItemCheck_Inner",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            binder: null, Type.EmptyTypes, modifiers: null);

        if (wrapped == null || inner == null || Snapshot == null)
        {
            Mod.Logger.Error("Held item snapshot clone: Player.ItemCheckWrapped(int), " +
                "ItemCheck_Inner() or lastVisualizedSelectedItem is missing, patch disabled");
            return;
        }

        // ItemCheck_Inner holds two clones, the guarded local one first and the remote one after
        // it. Only the remote one is rewritten, and only while vanilla's own guard still exists on
        // the other, since that guard is the whole argument for this being vanilla's test.
        MonoModHooks.Modify(wrapped, il => ReuseSnapshot(il, "ItemCheckWrapped", expected: 1,
            rewrite: 0, requiresGuard: false));
        MonoModHooks.Modify(inner, il => ReuseSnapshot(il, "ItemCheck_Inner", expected: 2,
            rewrite: 1, requiresGuard: true));
    }

    private void ReuseSnapshot(ILContext il, string method, int expected, int rewrite,
        bool requiresGuard)
    {
        List<Instruction> clones = il.Body.Instructions.Where(instruction =>
            instruction.MatchCallvirt(out MethodReference called)
            && called.Name == nameof(Item.Clone) && called.DeclaringType.Name == nameof(Item)
            && instruction.Next != null
            && instruction.Next.MatchStfld(out FieldReference stored)
            && stored.Name == Snapshot.Name).ToList();
        bool guarded = il.Body.Instructions.Any(instruction =>
            instruction.MatchCallvirt(out MethodReference called)
            && called.Name == "IsNotTheSameAs");

        if (clones.Count != expected || (requiresGuard && !guarded))
        {
            Mod.Logger.Error($"Held item snapshot clone: Player.{method} no longer clones the " +
                $"snapshot the way this patch was written against ({clones.Count} clones), " +
                "patch disabled");
            return;
        }

        // `held.Clone()` becomes `ReuseOrClone(held, lastVisualizedSelectedItem)`: the held item
        // is already on the stack, so only the snapshot is pushed before the call is redirected.
        Instruction clone = clones[rewrite];
        ILCursor cursor = new ILCursor(il);
        cursor.Goto(clone);
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Ldfld, Snapshot);
        clone.OpCode = OpCodes.Call;
        clone.Operand = il.Import(typeof(HeldItemSnapshotClone).GetMethod(nameof(ReuseOrClone)));
    }
}
