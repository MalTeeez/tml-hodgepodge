using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria.Graphics.Renderers;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// Vanilla's ParticlePool.RequestParticle restarts its search at index zero on every call:
//
//     int count = _particles.Count;
//     for (int i = 0; i < count; i++)
//         if (_particles[i].IsRestingInPool) { _particles[i].FetchFromPool(); return _particles[i]; }
//     T val = _instantiator();  _particles.Add(val);  val.FetchFromPool();  return val;
//
// A pool whose low indices are all in use therefore costs a walk past every one of them before it
// reaches a free slot, on every spawn. With a caller that asks once per tick per entity the pool
// grows long and the walk never gets shorter: measured at 86.45% of the client thread during a
// StarsAbove boss fight, 86.44% of it the loop itself, with only 5.2 ms reaching FetchFromPool.
//
// This resumes the search where the last one succeeded and wraps, which finds a free slot in a step
// or two instead of thousands. The contract is unchanged -- it returns a particle that was resting
// and marks it fetched -- but not necessarily the lowest indexed one, so particles are reused in a
// different order and drawn in a different order within a pool. Nothing resting still falls through
// to vanilla, which grows the pool exactly as before -- that case is scanned twice over, once here
// and once by the original body, which is the price of leaving vanilla's growth path alone.
//
// Off by default. This is the only patch here that rewrites a type every mod's particles pass
// through, and `tools/scan_particlepool.py` narrows the pack to what could notice rather than
// proving it safe. Take a clean scan as permission to test it, never as permission to keep it.
public class ParticlePoolScanCursor : Patch
{
    // One int per pool, keyed on the pool itself so a pool that goes away takes its cursor with it.
    private static readonly ConditionalWeakTable<object, int[]> Cursors = new();

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().ParticlePoolScanCursor;

    protected override void Apply()
    {
        MethodInfo request = typeof(ParticlePool<>).GetMethod(nameof(ParticlePool<IPooledParticle>
            .RequestParticle), BindingFlags.Instance | BindingFlags.Public);

        if (request == null)
        {
            Mod.Logger.Error("Particle pool scan cursor: ParticlePool.RequestParticle is missing, " +
                "patch disabled");
            return;
        }

        MonoModHooks.Modify(request, ResumeScan);
    }

    // Prepends a fast path and leaves the original body behind it as the fallback:
    //
    //     object found = TryFetch(_particles, this);
    //     if (found != null) return (T)found;
    //     ...original search and grow...
    //
    // The _particles field reference is copied off the instruction already in the body rather than
    // imported, because the field is on a generic type and the body's own operand is the one
    // already carrying the right instantiation.
    private void ResumeScan(ILContext il)
    {
        FieldReference particles = FindParticlesField(il);
        if (particles == null)
        {
            Mod.Logger.Error("Particle pool scan cursor: RequestParticle no longer reads a " +
                "particle list, left as it is");
            return;
        }

        ILCursor cursor = new(il);
        ILLabel notFound = cursor.DefineLabel();
        cursor.Goto(0, MoveType.Before);

        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Ldfld, particles);
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Call, typeof(ParticlePoolScanCursor)
            .GetMethod(nameof(TryFetch), BindingFlags.Static | BindingFlags.Public));
        cursor.Emit(OpCodes.Dup);
        cursor.Emit(OpCodes.Brfalse, notFound);

        // Unbox rather than castclass: the return type is the pool's generic parameter, which is
        // only a reference type for the instantiations in use, not by constraint.
        cursor.Emit(OpCodes.Unbox_Any, il.Method.ReturnType);
        cursor.Emit(OpCodes.Ret);

        // The null left by a miss is dropped here, and the original body picks up untouched. The
        // branch is pointed at this pop only once it exists, so it cannot land on the wrong one.
        cursor.Emit(OpCodes.Pop);
        notFound.Target = cursor.Prev;

        Mod.Logger.Info("Particle pool scan cursor: RequestParticle now resumes its search");
    }

    private static FieldReference FindParticlesField(ILContext il)
    {
        foreach (Instruction instruction in il.Body.Instructions)
            if (instruction.OpCode == OpCodes.Ldfld
                && instruction.Operand is FieldReference field && field.Name == "_particles")
                return field;

        return null;
    }

    // Public because the patched IL calls it. Returns a particle that was resting and is now
    // fetched, or null to let vanilla's own search and growth run unchanged.
    public static object TryFetch(object particles, object pool)
    {
        if (particles is not IList list)
            return null;

        int count = list.Count;
        if (count == 0)
            return null;

        int[] cursor = Cursors.GetValue(pool, _ => new int[1]);
        int start = cursor[0] < count ? cursor[0] : 0;

        for (int offset = 0; offset < count; offset++)
        {
            int index = start + offset;
            if (index >= count)
                index -= count;

            if (list[index] is not IPooledParticle particle || !particle.IsRestingInPool)
                continue;

            cursor[0] = index + 1 == count ? 0 : index + 1;
            particle.FetchFromPool();
            return particle;
        }

        return null;
    }
}
