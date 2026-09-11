using System;
using System.Collections.Concurrent;
using System.Reflection;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// Daybreak replaces tModLoader's ModType.Name getter at module initialisation so a mod can name its
// own content:
//
//     MonoModHooks.Add(typeof(ModType).GetProperty("Name").GetMethod,
//         (Func<ModType, string>)(self => GetName(self.Mod, self.GetType())));
//
//     public static string GetName(Mod mod, Type type) =>
//         mod is INameProvider provider ? provider.GetName(type) : GetDefaultName(type);
//     public static string GetDefaultName(Type type) => type.Name;
//
// Every mod that does not implement INameProvider lands on type.Name, which is not a field read. It
// goes through RuntimeType.GetCachedName, whose cache the runtime drops on collection, so under
// this pack's allocation rate most reads rebuild it. The blood moon capture puts the hook at 1.04%
// of the thread with 1.41% inside RuntimeType.InitializeCache beneath it, read per NPC per tick by
// TimeFrozenPrevention, ShrimpMissileFix, PutridPinkyNerf and ThrowerUnification among others.
//
// The cache sits in front of the getter rather than on GetDefaultName, which would look like the
// smaller change and would do nothing. Daybreak's lambda already carries GetName and GetDefaultName
// inlined into it by the time any patch here runs -- that is exactly what the profile shows, a
// frame for the lambda with GetCachedName directly beneath it and nothing in between -- so
// rewriting either of those methods leaves the inlined copy the hot path actually executes.
//
// A mod naming its own content is asked every time, because what an INameProvider answers is its
// own business and may depend on state. Everything else resolves to GetType().Name, which cannot
// change for a given type, so remembering it is the same answer.
//
// This ships off by default because it detours a tModLoader property every mod's content reads,
// which is the bar this project sets for shared infrastructure. What it can reach is narrow enough
// to state rather than survey: a ModType that overrides Name never reaches the base getter and so
// is untouched, and an INameProvider mod is exempted above, which leaves only the types whose name
// is a constant.
//
// Read against Daybreak 2026.4.
public class ModTypeNameCache : Patch
{
    private const string TargetMod = "Daybreak";
    private const string ProviderType = "Daybreak.Common.Features.Models.INameProvider";

    private static readonly ConcurrentDictionary<Type, string> Names = new();

    private static Type _nameProvider;

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().ModTypeNameCache;

    public static string CachedName(Func<ModType, string> orig, ModType self)
    {
        // Asking the mod rather than the cache is the whole exemption: a name provider may answer
        // differently for the same type from one call to the next.
        if (_nameProvider.IsInstanceOfType(self.Mod))
            return orig(self);

        // The factory can run twice for one type under contention. Both runs ask orig for the same
        // constant, so either answer is the right one.
        return Names.GetOrAdd(self.GetType(), _ => orig(self));
    }

    protected override void Apply()
    {
        // Without Daybreak the getter is tModLoader's own `GetType().Name`, which is the same cost
        // and the same constant, but patching it then would be a change to tModLoader alone rather
        // than to the mod that made this path hot. Left for a capture that shows it.
        if (!ModLoader.TryGetMod(TargetMod, out Mod daybreak))
            return;

        _nameProvider = daybreak.Code.GetType(ProviderType);
        MethodInfo getter = typeof(ModType).GetProperty(nameof(ModType.Name))?.GetMethod;

        if (_nameProvider == null || getter == null)
        {
            Mod.Logger.Error("Mod type name cache: ModType.Name or Daybreak's INameProvider is " +
                "missing, patch disabled");
            return;
        }

        // Types from the previous load are gone after a reload, and holding them would keep their
        // assemblies alive as well as answering with stale names.
        Names.Clear();

        int before = DetourCount(getter);
        MonoModHooks.Add(getter, CachedName);

        // Daybreak hooks the same getter from a module initializer, so this one has to land on top
        // of it to be the one that answers. A count that did not move means it did not attach and
        // the cache is never consulted, which is invisible without the check.
        if (DetourCount(getter) <= before)
            Mod.Logger.Error("Mod type name cache: the cache did not attach to ModType.Name, so " +
                "names are still resolved through reflection on every read");
    }
}
