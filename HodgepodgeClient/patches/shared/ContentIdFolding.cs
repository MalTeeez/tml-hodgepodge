using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// Content ids are handed out once when mods finish loading and never change afterwards, yet three
// separate mods in this pack resolve them on a per tile, per NPC or per frame path. Two spellings
// turn up:
//
//     ModContent.NPCType<SupremeCalamitas>()          a generic lookup on a type
//     someMod.Find<ModNPC>("Astrageldon").Type        a dictionary lookup on a name
//
// Both fold to the integer they return. This runs from PostSetupContent, by which point every id
// is assigned, and it runs again on a reload, when they may be different.
//
// Folding is per call site and independent: a lookup that cannot be resolved is left as a call
// rather than taking the rest of the method with it. A method where nothing at all folded is a
// method that no longer looks the way the patch was written against, which is the caller's cue to
// log and leave it alone.
internal static class ContentIdFolding
{
    // The `static int Name<T>()` lookups on ModContent. Others exist; these are the ones the
    // patched methods use, and an unlisted one is simply left as a call.
    private static readonly string[] ContentLookups =
        ["NPCType", "ItemType", "BuffType", "ProjectileType", "TileType", "WallType"];

    // Folds `ModContent.XType<T>()` call sites into their ids. `resolveType` turns a Cecil type
    // reference into a loaded Type, because Cecil has no resolver for a mod assembly and throws on
    // every argument if asked to do it itself.
    public static int FoldContentLookups(ILContext il, Func<TypeReference, Type> resolveType,
        Action<string> onSkipped)
    {
        int folded = 0;
        foreach (Instruction instruction in il.Body.Instructions)
        {
            if (instruction.OpCode != OpCodes.Call
                || instruction.Operand is not GenericInstanceMethod lookup
                || !ContentLookups.Contains(lookup.ElementMethod.Name)
                || lookup.ElementMethod.DeclaringType.Name != nameof(ModContent))
                continue;

            int? id = ResolveContentId(lookup, resolveType, onSkipped);
            if (id == null)
                continue;

            instruction.OpCode = OpCodes.Ldc_I4;
            instruction.Operand = id.Value;
            folded++;
        }

        return folded;
    }

    // Folds `<mod expression>.Find<T>("Name").Type` chains into their ids. The chain is four
    // instructions -- the call that produces the Mod, the name, the Find, the Type getter -- and
    // only the ones whose Mod comes from a parameterless static call can be evaluated here, since
    // anything else depends on runtime state this cannot see.
    //
    // The three leading instructions become no-ops rather than being removed, so a branch that
    // targeted any of them still lands somewhere valid. The JIT drops them.
    public static int FoldNamedFinds(ILContext il, Action<string> onSkipped)
    {
        int folded = 0;
        foreach (Instruction instruction in il.Body.Instructions.ToArray())
        {
            if (!IsTypeGetter(instruction)
                || instruction.Previous is not { } find || !IsNamedFind(find)
                || find.Previous is not { } name || name.OpCode != OpCodes.Ldstr
                || name.Previous is not { } source || !ProducesMod(source))
                continue;

            int? id = ResolveFoundId(source, (string)name.Operand,
                ((GenericInstanceMethod)find.Operand).GenericArguments[0], onSkipped);
            if (id == null)
                continue;

            source.OpCode = OpCodes.Nop;
            source.Operand = null;
            name.OpCode = OpCodes.Nop;
            name.Operand = null;
            find.OpCode = OpCodes.Nop;
            find.Operand = null;
            instruction.OpCode = OpCodes.Ldc_I4;
            instruction.Operand = id.Value;
            folded++;
        }

        return folded;
    }

    private static bool IsTypeGetter(Instruction instruction) =>
        (instruction.OpCode == OpCodes.Callvirt || instruction.OpCode == OpCodes.Call)
        && instruction.Operand is MethodReference getter && getter.Name == "get_Type"
        && getter.Parameters.Count == 0;

    private static bool IsNamedFind(Instruction instruction) =>
        (instruction.OpCode == OpCodes.Callvirt || instruction.OpCode == OpCodes.Call)
        && instruction.Operand is GenericInstanceMethod find
        && find.ElementMethod.Name == nameof(Mod.Find)
        && find.ElementMethod.DeclaringType.Name == nameof(Mod);

    // A parameterless static call, which is what a crossmod helper property compiles to. Its
    // answer is fixed for the life of a load, so calling it now gives the same Mod it would give
    // on every later tick.
    private static bool ProducesMod(Instruction instruction) =>
        instruction.OpCode == OpCodes.Call && instruction.Operand is MethodReference producer
        && producer.Parameters.Count == 0 && producer.HasThis == false
        && producer.ReturnType.Name == nameof(Mod);

    private static int? ResolveContentId(GenericInstanceMethod lookup,
        Func<TypeReference, Type> resolveType, Action<string> onSkipped)
    {
        TypeReference argument = lookup.GenericArguments[0];
        try
        {
            Type content = resolveType(argument);
            MethodInfo generic = typeof(ModContent)
                .GetMethod(lookup.ElementMethod.Name, BindingFlags.Static | BindingFlags.Public);

            if (content == null || generic == null)
            {
                onSkipped($"{argument.Name} did not resolve to a loaded content type");
                return null;
            }

            return (int)generic.MakeGenericMethod(content).Invoke(null, null);
        }
        catch (Exception exception)
        {
            onSkipped($"{argument.Name} did not resolve, {exception.Message}");
            return null;
        }
    }

    private static int? ResolveFoundId(Instruction source, string name, TypeReference contentKind,
        Action<string> onSkipped)
    {
        try
        {
            MethodInfo producer = ResolveProducer((MethodReference)source.Operand);
            Type kind = ResolveContentKind(contentKind);

            if (producer == null || kind == null)
            {
                onSkipped($"the mod behind Find<{contentKind.Name}>(\"{name}\") is not reachable");
                return null;
            }

            // The mod being absent is not an error: the call site sits behind its own Loaded test,
            // so leaving it as a call keeps the original behaviour, whatever that is.
            if (producer.Invoke(null, null) is not Mod mod)
                return null;

            MethodInfo find = typeof(Mod).GetMethod(nameof(Mod.Find), BindingFlags.Instance
                | BindingFlags.Public).MakeGenericMethod(kind);

            // Every content kind declares its own int Type rather than inheriting one, so it is
            // read off the concrete class rather than through a shared interface.
            return find.Invoke(mod, [name]) is ModType content
                ? (int)content.GetType().GetProperty("Type").GetValue(content) : null;
        }
        catch (Exception exception)
        {
            onSkipped($"Find<{contentKind.Name}>(\"{name}\") did not resolve, {exception.Message}");
            return null;
        }
    }

    // The producing call lives in the patched mod's own assembly, so it is found by walking the
    // types already loaded rather than through Cecil.
    private static MethodInfo ResolveProducer(MethodReference producer)
    {
        Type owner = ResolveLoadedType(producer.DeclaringType);
        return owner?.GetMethod(producer.Name, BindingFlags.Static | BindingFlags.Public
            | BindingFlags.NonPublic, binder: null, Type.EmptyTypes, modifiers: null);
    }

    private static Type ResolveContentKind(TypeReference contentKind) =>
        typeof(Mod).Assembly.GetType(contentKind.FullName.Replace('/', '+'));

    // Cecil spells a nested type with a slash where reflection wants a plus, and names the
    // assembly separately from the type.
    private static Type ResolveLoadedType(TypeReference type)
    {
        string name = type.FullName.Replace('/', '+');
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name == type.Scope.Name.Replace(".dll", string.Empty))
                return assembly.GetType(name);
        }

        return AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name)).FirstOrDefault(found => found != null);
    }

    // Resolving against one mod's own assembly, which is what every patch here needs: the content
    // a mod looks up by type is content it declares or borrows from a reference it already loads.
    public static Func<TypeReference, Type> InAssembly(Assembly code) => argument =>
        code.GetType(argument.FullName.Replace('/', '+')) ?? ResolveLoadedType(argument);

    // Shared reporting so each patch says the same thing about a fold that did not happen, and
    // logs the count when it did.
    public static void Report(Mod mod, string label, string method, int folded,
        IReadOnlyCollection<string> skipped)
    {
        foreach (string reason in skipped)
            mod.Logger.Error($"{label}: {reason}, left as a call");

        if (folded == 0)
            mod.Logger.Error($"{label}: {method} no longer resolves any content id the way this " +
                "patch was written against, left as it is");
        else
            mod.Logger.Info($"{label}: folded {folded} lookups in {method} into constants");
    }
}
