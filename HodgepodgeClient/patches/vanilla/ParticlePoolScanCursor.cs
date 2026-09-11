using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        HashSet<Type> poolTypes = FindClosedPoolTypes();

        if (poolTypes.Count == 0)
        {
            Mod.Logger.Error("Particle pool scan cursor: no closed ParticlePool<T> types were " +
                "found, patch disabled");
            return;
        }

        int hooked = 0;
        foreach (Type poolType in poolTypes.OrderBy(type => type.FullName))
        {
            MethodInfo request = poolType.GetMethod(nameof(ParticlePool<IPooledParticle>
                .RequestParticle), BindingFlags.Instance | BindingFlags.Public);
            if (request == null)
            {
                Mod.Logger.Error($"Particle pool scan cursor: {poolType.FullName}." +
                    "RequestParticle is missing, that particle type was not patched");
                continue;
            }

            try
            {
                MonoModHooks.Modify(request, ResumeScan);
                hooked++;
            }
            catch (Exception exception)
            {
                Mod.Logger.Error($"Particle pool scan cursor: {poolType.FullName} could not be " +
                    $"patched and was left unchanged, {exception}");
            }
        }

        if (hooked == 0)
            Mod.Logger.Error("Particle pool scan cursor: none of the closed pool types could be " +
                "patched, patch disabled");
        else
            Mod.Logger.Info($"Particle pool scan cursor: patched {hooked} closed pool types");
    }

    // MonoMod cannot build a trampoline for a method on the open ParticlePool<T> definition.
    // Persistent pools appear in member signatures, so collect every closed construction from
    // vanilla and the loaded mods and patch its RequestParticle separately.
    private static HashSet<Type> FindClosedPoolTypes()
    {
        HashSet<Type> poolTypes = new();
        HashSet<Type> scannedConstructions = new();
        IEnumerable<Assembly> assemblies = ModLoader.Mods.Select(mod => mod.Code)
            .Append(typeof(ParticlePool<>).Assembly).Distinct();

        foreach (Assembly assembly in assemblies)
        foreach (Type type in LoadableTypes(assembly))
        {
            try
            {
                CollectDeclaredPoolTypes(type, poolTypes);
                CollectConstructedHierarchy(type.BaseType, poolTypes, scannedConstructions);
                foreach (Type implemented in type.GetInterfaces())
                    CollectConstructedHierarchy(implemented, poolTypes, scannedConstructions);
            }
            catch (FileNotFoundException)
            {
                // An unrelated type can name an optional mod assembly that is not enabled. Its
                // signatures cannot contain a usable pool, but must not stop the rest of the scan.
            }
            catch (TypeLoadException)
            {
                // As above, for an unresolved type rather than its containing assembly.
            }
        }

        return poolTypes;
    }

    private static void CollectDeclaredPoolTypes(Type type, HashSet<Type> poolTypes)
    {
        const BindingFlags declared = BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        foreach (FieldInfo field in type.GetFields(declared))
            CollectPoolType(field.FieldType, poolTypes);
        foreach (PropertyInfo property in type.GetProperties(declared))
            CollectPoolType(property.PropertyType, poolTypes);
        foreach (MethodInfo method in type.GetMethods(declared))
            CollectMethodTypes(method, poolTypes);
    }

    // A generic owner can declare ParticlePool<T> while a non-generic subclass closes T. Reflection
    // substitutes that argument when its inherited constructed type is inspected, which finds the
    // CalamityHunt Particle<T> pools that a scan of the open declaration alone cannot hook.
    private static void CollectConstructedHierarchy(Type type, HashSet<Type> poolTypes,
        HashSet<Type> scannedConstructions)
    {
        if (type?.IsConstructedGenericType != true || !scannedConstructions.Add(type))
            return;

        CollectPoolType(type, poolTypes);
        CollectDeclaredPoolTypes(type, poolTypes);
        CollectConstructedHierarchy(type.BaseType, poolTypes, scannedConstructions);
        foreach (Type implemented in type.GetInterfaces())
            CollectConstructedHierarchy(implemented, poolTypes, scannedConstructions);
    }

    private static Type[] LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type != null).ToArray();
        }
    }

    // Include parameters and returns so a pool exposed by a factory rather than held in a field
    // still contributes its closed type. A method-local pool cannot retain a long scan prefix
    // between calls unless it escapes through one of these signatures.
    private static void CollectMethodTypes(MethodBase method, HashSet<Type> poolTypes)
    {
        if (method is MethodInfo methodInfo)
            CollectPoolType(methodInfo.ReturnType, poolTypes);
        foreach (ParameterInfo parameter in method.GetParameters())
            CollectPoolType(parameter.ParameterType, poolTypes);
    }

    private static void CollectPoolType(Type signature, HashSet<Type> poolTypes)
    {
        if (signature.HasElementType)
        {
            CollectPoolType(signature.GetElementType(), poolTypes);
            return;
        }

        if (!signature.IsGenericType)
            return;

        if (signature.GetGenericTypeDefinition() == typeof(ParticlePool<>))
        {
            if (!signature.ContainsGenericParameters)
                poolTypes.Add(signature);
            return;
        }

        foreach (Type argument in signature.GetGenericArguments())
            CollectPoolType(argument, poolTypes);
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
