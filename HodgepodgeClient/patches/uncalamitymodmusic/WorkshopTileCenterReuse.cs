using System.Collections;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// WorkshopDetection is a GlobalTile that records the centre of every nearby tile so the music can
// ask how far away the closest crafting station is:
//
//     if (closer) { ...; tileCenters[type].Add(centre); }
//     else        { tileCenters.Clear(); }
//
// SceneMetrics.ScanAndExportToMain scans twice -- once over the wide area with closer:false, then
// over the tight one with closer:true -- so every scan clears the dictionary once per wide-area
// tile, throwing away every list along with the capacity it had grown, and then rebuilds each list
// from empty by repeated doubling. Measured, the method is 85.3% List<Vector2> growth, 12.8%
// dictionary churn and 0.0% its own code: 0.153 ms/frame, over half of everything
// TileLoader.NearbyEffects costs.
//
// Emptying the lists in place instead keeps the capacity, so after the first scan nothing
// reallocates. It is the same state to every reader: a key holding an empty list iterates exactly
// as a missing key does, and the add path finds the key already there.
public class WorkshopTileCenterReuse : Patch
{
    private const string TargetMod = "UnCalamityModMusic";
    private const string TargetType = "UnCalamityModMusic.Common.WorkshopDetection";
    private const string CentersField = "tileCenters";

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().WorkshopTileCenterReuse;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod unCalamityMusic))
            return;

        System.Type detection = unCalamityMusic.Code.GetType(TargetType);
        MethodInfo nearbyEffects = detection?.GetMethod("NearbyEffects",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        FieldInfo centers = detection?.GetField(CentersField,
            BindingFlags.Static | BindingFlags.NonPublic);

        if (nearbyEffects == null || centers == null
            || !typeof(IDictionary).IsAssignableFrom(centers.FieldType))
        {
            Mod.Logger.Error($"Workshop tile centre reuse: {TargetType}.NearbyEffects or a " +
                $"dictionary {CentersField} is missing, patch disabled");
            return;
        }

        if (!ReadsField(nearbyEffects, centers))
        {
            Mod.Logger.Error($"Workshop tile centre reuse: NearbyEffects no longer reads " +
                $"{CentersField}, patch disabled");
            return;
        }

        MonoModHooks.Modify(nearbyEffects, ReuseTheLists);
    }

    // Rewrites the one call rather than the branch around it, so when the lists are emptied, and
    // on which tiles, stays exactly as the mod wrote it.
    private void ReuseTheLists(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.Before, instruction =>
                instruction.MatchCallvirt(out MethodReference called) && called.Name == "Clear"
                && called.DeclaringType.Name.StartsWith("Dictionary")))
        {
            Mod.Logger.Error("Workshop tile centre reuse: NearbyEffects no longer clears the " +
                "dictionary, patch disabled");
            return;
        }

        cursor.Next.OpCode = OpCodes.Call;
        cursor.Next.Operand = il.Import(
            typeof(WorkshopTileCenterReuse).GetMethod(nameof(EmptyEachList)));
    }

    // List<T>.Clear on a T holding no references only resets the count, so this keeps every array
    // that has already been grown and costs nothing per call beyond walking the handful of keys.
    public static void EmptyEachList(IDictionary centers)
    {
        foreach (object list in centers.Values)
            ((IList)list).Clear();
    }
}
