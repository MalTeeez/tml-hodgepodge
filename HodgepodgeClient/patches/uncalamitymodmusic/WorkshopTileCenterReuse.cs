using System.Collections;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// WorkshopDetection is a GlobalTile that records the centre of every nearby tile so the music can
// ask how far away the closest crafting station is:
//
//     if (closer) { ...; tileCenters[type].Add(centre); }
//     else        { tileCenters.Clear(); }
//
// SceneMetrics.ScanAndExportToMain scans twice -- once over the wide area with closer:false, then
// over the tight one with closer:true -- so the dictionary is cleared once per wide-area tile,
// throwing away every list along with the capacity it had grown, and the tight pass then rebuilds
// each list from empty by repeated doubling.
//
// Emptying the lists in place keeps the capacity, so after the first scan nothing reallocates. It
// is the same state to every reader: a key holding an empty list iterates exactly as a missing key
// does, and the add path finds the key already there.
//
// The emptying belongs at the start of a scan rather than at the call site it replaces.
// Dictionary.Clear opens with `if (_count > 0)`, so of the thousands of calls a wide-area pass
// makes only the first does any work, and a replacement without that short circuit walks every key
// on every tile -- far more than the regrowth it set out to save. So the lists are emptied once in
// ScanAndExportToMain, which is where a scan begins, and the per-tile call is dropped.
//
// The one visible difference is a wide-area pass that touches no tile at all, out in open sky. The
// mod would leave the previous scan's centres standing where this empties them, which is the answer
// the scan would have given had it found anything to clear.
public class WorkshopTileCenterReuse : Patch
{
    private const string TargetMod = "UnCalamityModMusic";
    private const string TargetType = "UnCalamityModMusic.Common.WorkshopDetection";
    private const string CentersField = "tileCenters";
    private const string ScanMethod = "ScanAndExportToMain";

    private static FieldInfo _centers;
    private static bool _hoisted;

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().WorkshopTileCenterReuse;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod unCalamityMusic))
            return;

        System.Type detection = unCalamityMusic.Code.GetType(TargetType);
        MethodInfo nearbyEffects = detection?.GetMethod("NearbyEffects",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        _centers = detection?.GetField(CentersField,
            BindingFlags.Static | BindingFlags.NonPublic);

        if (nearbyEffects == null || _centers == null
            || !typeof(IDictionary).IsAssignableFrom(_centers.FieldType))
        {
            Mod.Logger.Error($"Workshop tile centre reuse: {TargetType}.NearbyEffects or a " +
                $"dictionary {CentersField} is missing, patch disabled");
            return;
        }

        if (!ReadsField(nearbyEffects, _centers))
        {
            Mod.Logger.Error($"Workshop tile centre reuse: NearbyEffects no longer reads " +
                $"{CentersField}, patch disabled");
            return;
        }

        MethodInfo scan = typeof(SceneMetrics).GetMethod(ScanMethod,
            BindingFlags.Instance | BindingFlags.Public);
        if (scan == null)
        {
            Mod.Logger.Error($"Workshop tile centre reuse: SceneMetrics.{ScanMethod} is missing, " +
                "patch disabled");
            return;
        }

        // Static, so a reload would otherwise carry the previous load's result into this one.
        _hoisted = false;
        MonoModHooks.Modify(scan, EmptyOncePerScan);

        // Only with the scan emptying the lists is it safe to take the per-tile clear away.
        if (!_hoisted)
        {
            Mod.Logger.Error($"Workshop tile centre reuse: the per-scan empty did not inject into " +
                $"{ScanMethod}, the mod's own clear is left in place");
            return;
        }

        MonoModHooks.Modify(nearbyEffects, DropThePerTileClear);
    }

    private void EmptyOncePerScan(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);
        cursor.Emit(OpCodes.Ldsfld, _centers);
        cursor.Emit(OpCodes.Call, typeof(WorkshopTileCenterReuse).GetMethod(nameof(EmptyEachList)));
        _hoisted = true;
    }

    // The clear becomes a pop, discarding the dictionary the call would have consumed. Rewriting
    // the call rather than the branch around it leaves the rest of the tile's work as the mod
    // wrote it, so only the emptying moves.
    private void DropThePerTileClear(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.Before, instruction =>
                instruction.MatchCallvirt(out MethodReference called) && called.Name == "Clear"
                && called.DeclaringType.Name.StartsWith("Dictionary")))
        {
            Mod.Logger.Error("Workshop tile centre reuse: NearbyEffects no longer clears the " +
                "dictionary, so its lists are emptied per scan on top of whatever it does instead");
            return;
        }

        cursor.Next.OpCode = OpCodes.Pop;
        cursor.Next.Operand = null;
    }

    // Runs once per scan. List<T>.Clear on a T holding no references only resets the count, so
    // every array that has already been grown survives into the next scan.
    public static void EmptyEachList(IDictionary centers)
    {
        foreach (object list in centers.Values)
            ((IList)list).Clear();
    }
}
