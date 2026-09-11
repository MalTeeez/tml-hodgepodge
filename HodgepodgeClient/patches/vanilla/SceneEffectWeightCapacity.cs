using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// SceneEffectLoader.UpdateSceneEffect weighs every registered scene effect against the player once
// per tick, and collects the active ones in a list it builds from empty every time:
//
//     List<AtmosWeight> list = new List<AtmosWeight>();
//     for (...) if (flag) list.Add(new AtmosWeight(...));
//
// A list with no capacity grows by doubling, so a tick with twenty active effects allocates five
// arrays and copies four of them to reach the one it keeps. On the profiled client that was
// 0.294 ms/frame, 1.08% of the thread, all of it inside List.AddWithResize and set_Capacity.
//
// The fix is the capacity the list was never given. One array of a fixed size replaces the chain,
// and it is smaller than the arrays the chain allocates on the way to the same length. Nothing
// about the list's contents or the scene effects chosen from it changes.
//
// Every mod's scene effects run through this tModLoader method, but capacity is not observable:
// there is no render state, ordering or value here for another mod to inherit.
public class SceneEffectWeightCapacity : Patch
{
    // Above the count of scene effects active at once in the profiled pack, and small enough that
    // one array of it costs less than the four the doubling chain throws away reaching that length.
    // A tick that needs more than this grows once, as it does today.
    private const int Capacity = 32;

    private const string WeightType = "AtmosWeight";

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().SceneEffectWeightCapacity;

    protected override void Apply()
    {
        MethodInfo update = typeof(SceneEffectLoader).GetMethod(
            nameof(SceneEffectLoader.UpdateSceneEffect),
            BindingFlags.Instance | BindingFlags.Public, binder: null, [typeof(Player)],
            modifiers: null);

        if (update == null)
        {
            Mod.Logger.Error("Scene effect weight capacity: SceneEffectLoader.UpdateSceneEffect" +
                "(Player) is missing, patch disabled");
            return;
        }

        MonoModHooks.Modify(update, PresizeTheWeightList);
    }

    private void PresizeTheWeightList(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.Before, IsEmptyWeightList))
        {
            Mod.Logger.Error("Scene effect weight capacity: UpdateSceneEffect no longer builds " +
                $"its {WeightType} list from empty, patch disabled");
            return;
        }

        // The same list type, reached through its other constructor. Cecil takes the capacity
        // overload as a reference against the instantiated type rather than a resolved definition.
        MethodReference empty = (MethodReference)cursor.Next.Operand;
        MethodReference sized = new MethodReference(empty.Name, empty.ReturnType, empty.DeclaringType)
        {
            HasThis = true,
        };
        sized.Parameters.Add(new ParameterDefinition(il.Import(typeof(int))));

        cursor.Emit(OpCodes.Ldc_I4, Capacity);
        cursor.Next.Operand = sized;
    }

    // The method builds a SceneEffectInstance before it builds this list, so matching on the weight
    // type rather than on the first parameterless constructor is what keeps the two apart.
    private static bool IsEmptyWeightList(Instruction instruction) =>
        instruction.MatchNewobj(out MethodReference constructor)
        && constructor.Parameters.Count == 0
        && constructor.DeclaringType is GenericInstanceType list
        && list.GenericArguments.Count == 1
        && list.GenericArguments[0].Name == WeightType;
}
