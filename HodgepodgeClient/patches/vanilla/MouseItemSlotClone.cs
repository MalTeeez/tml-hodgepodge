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

// Player.dropItemCheck copies Main.mouseItem into inventory slot 58 every local-player tick:
//
//     inventory[58] = Main.mouseItem.Clone();
//
// Slot 58 is the temporary held-item slot while an item is on the cursor. Each clone also clones
// every GlobalItem in the pack, even when the mouse item's type, stack and prefix are unchanged.
// Reuse the previous slot value under the same key vanilla uses for held-item snapshots.
//
// A mod can still put per-instance state outside that key on the mouse item and expect slot 58 to
// receive it that tick. Keep this separately switchable because slot 58 participates in item use
// as well as drawing.
public class MouseItemSlotClone : Patch
{
    private const int MouseItemSlot = 58;

    private static readonly FieldInfo Inventory = typeof(Player).GetField(nameof(Player.inventory));

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().MouseItemSlotClone;

    protected override void Apply()
    {
        MethodInfo dropItemCheck = typeof(Player).GetMethod(nameof(Player.dropItemCheck),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
            binder: null, Type.EmptyTypes, modifiers: null);
        if (dropItemCheck == null || Inventory == null)
        {
            Mod.Logger.Error("Mouse item slot clone: Player.dropItemCheck() or inventory is " +
                "missing, patch disabled");
            return;
        }

        MonoModHooks.Modify(dropItemCheck, ReuseSlotCopy);
    }

    private void ReuseSlotCopy(ILContext il)
    {
        List<Instruction> copies = il.Body.Instructions.Where(IsMouseItemSlotClone).ToList();
        if (copies.Count != 1)
        {
            Mod.Logger.Error("Mouse item slot clone: Player.dropItemCheck no longer copies the " +
                $"mouse item into slot {MouseItemSlot} once ({copies.Count} matches), patch disabled");
            return;
        }

        // The array, index and mouse item are already on the stack. Push the old array element and
        // turn Clone() into ReuseOrClone(mouseItem, inventory[58]).
        Instruction clone = copies[0];
        ILCursor cursor = new(il);
        cursor.Goto(clone);
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Ldfld, Inventory);
        cursor.Emit(OpCodes.Ldc_I4, MouseItemSlot);
        cursor.Emit(OpCodes.Ldelem_Ref);
        clone.OpCode = OpCodes.Call;
        clone.Operand = il.Import(typeof(HeldItemSnapshotClone).GetMethod(
            nameof(HeldItemSnapshotClone.ReuseOrClone)));
    }

    private static bool IsMouseItemSlotClone(Instruction instruction) =>
        instruction.MatchCallvirt(out MethodReference called)
        && called.Name == nameof(Item.Clone) && called.DeclaringType.Name == nameof(Item)
        && instruction.Previous?.MatchLdsfld(out FieldReference source) == true
        && source.Name == nameof(Main.mouseItem) && source.DeclaringType.Name == nameof(Main)
        && instruction.Previous.Previous?.MatchLdcI4(MouseItemSlot) == true
        && instruction.Previous.Previous.Previous?.MatchLdfld(out FieldReference inventory) == true
        && inventory.Name == Inventory.Name
        && instruction.Previous.Previous.Previous.Previous?.MatchLdarg(0) == true
        && instruction.Next?.OpCode == OpCodes.Stelem_Ref;
}
