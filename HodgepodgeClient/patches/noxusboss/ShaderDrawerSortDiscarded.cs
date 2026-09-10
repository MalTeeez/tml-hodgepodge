using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// InterfacedEntityDrawSystem.DrawInterfaceProjectiles runs on every DrawProjectiles, and its third
// line is a mistake:
//
//     List<IDrawsWithShader> list = projectileShaderDrawers.ToList();
//     list.AddRange(npcShaderDrawers);
//     list.OrderBy(i => i.LayeringPriority).ToList();     // result never assigned
//
// OrderBy does not sort in place -- it returns a new sequence -- so that line sorts the list,
// allocates a second one to hold the result, and drops both. The list it is sorting was just built
// by two LINQ passes over the projectile and NPC arrays, so it is not short.
//
// The sort and the allocation go; the two LINQ passes stay, because their results are used. What
// is left is exactly what the mod already does, since nothing ever read the sorted copy.
public class ShaderDrawerSortDiscarded : Patch
{
    private const string TargetMod = "NoxusBoss";
    private const string TargetType =
        "NoxusBoss.Core.Graphics.Automators.InterfacedEntityDrawSystem";

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().ShaderDrawerSortDiscarded;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod noxusBoss))
            return;

        MethodInfo draw = noxusBoss.Code.GetType(TargetType)?.GetMethod("DrawInterfaceProjectiles",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

        if (draw == null)
        {
            Mod.Logger.Error($"Shader drawer sort discarded: {TargetType}." +
                "DrawInterfaceProjectiles is missing, patch disabled");
            return;
        }

        MonoModHooks.Modify(draw, DropTheDiscardedSort);
    }

    // The sequence is `call OrderBy`, `call ToList`, `pop`. Turning the two calls into pops
    // discards the same two stack values the calls would have consumed, so the trailing pop is one
    // too many and goes. The lambda that was being sorted by is still constructed, which costs a
    // field read -- leaving it there keeps the edit to the three instructions that do the work.
    private void DropTheDiscardedSort(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.Before,
                i => IsLinqCall(i, "OrderBy"),
                i => IsLinqCall(i, "ToList"),
                i => i.MatchPop()))
        {
            Mod.Logger.Error("Shader drawer sort discarded: DrawInterfaceProjectiles no longer " +
                "throws away an OrderBy, patch disabled");
            return;
        }

        cursor.Next.OpCode = OpCodes.Pop;           // was OrderBy: drops the key selector
        cursor.Next.Operand = null;
        cursor.Index++;
        cursor.Next.OpCode = OpCodes.Pop;           // was ToList: drops the list
        cursor.Next.Operand = null;
        cursor.Index++;
        cursor.Remove();                            // the original pop now has nothing to take
    }

    private static bool IsLinqCall(Instruction instruction, string name) =>
        instruction.MatchCall(out MethodReference called) && called.Name == name
        && called.DeclaringType.FullName == "System.Linq.Enumerable";
}
