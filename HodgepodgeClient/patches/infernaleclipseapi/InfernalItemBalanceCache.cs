using System;
using System.Collections.Concurrent;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// InfernalItemBalanceChange.SetDefaults is a 4302 line GlobalItem hook that rebalances several
// hundred crossmod items. It asks which item it has been handed through one of two static helpers,
// hundreds of times per call:
//
//     public static bool GetItem(Mod mod, string name, Item item) {
//         ModLoader.TryGetMod("CalamityBardHealer", ref val);
//         ModLoader.TryGetMod("ThoriumMod", ref val2);
//         ModLoader.TryGetMod("ThoriumRework", ref val3);
//         if (mod == val || mod == val2 || mod == val3) { if (!mod.TryFind<ModItem>(name, ref val4)) return false; }
//         else { val4 = mod.Find<ModItem>(name); }
//         return item.type == val4.Type;
//     }
//
// Four dictionary lookups, every call, to compute `item.type == <a number fixed since load>`.
// That is why Dictionary is the second heaviest frame in the blood moon capture at 3.68% of the
// thread, with GetItem its largest caller at 1.48%. Blood moon drives it because every enemy drop
// arrives through NetMessage.CheckBytes into Item.SetDefaults, which is 3.91% of that thread.
//
// The method itself is untouched. Only the helper it calls changes, to answer from a table built
// on first use. Ids do not change after mods finish loading, so the answer is the same one the
// original computes.
//
// Two details of the original are kept rather than tidied. A mod outside the three named ones
// reaches Find, which throws when the name is wrong, so a miss there is re-raised by calling Find
// exactly as before instead of being swallowed into a false. And the three named mods are resolved
// once here, which is the only behaviour this changes: the original re-reads them per call, and a
// mod cannot load or unload between two calls within a tick.
//
// Read against InfernalEclipseAPI 0.10.8.
public class InfernalItemBalanceCache : Patch
{
    private const string TargetMod = "InfernalEclipseAPI";
    private const string TargetType =
        "InfernalEclipseAPI.Common.Globals.GlobalItems.InfernalItemBalanceChange";

    // The three mods the original treats as optional, where a missing name is answered with false
    // rather than an exception.
    private static readonly string[] TolerantMods =
        ["CalamityBardHealer", "ThoriumMod", "ThoriumRework"];

    // Item ids by the (mod, name) pair the mod asks with. Concurrent because item defaults are set
    // from the network read as well as the update path, and a torn read here would hand back the
    // wrong item.
    private static readonly ConcurrentDictionary<(Mod, string), int> Ids = new();

    private static Mod[] _tolerant = [];

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().InfernalItemBalanceCache;

    // Stands in for GetItem. A name the mod does not have is -1 for a tolerant mod, and for any
    // other mod Find has already thrown by the time the table is filled.
    public static bool GetItem(Mod mod, string name, Item item) =>
        item.type == Ids.GetOrAdd((mod, name), ResolveId);

    // Stands in for UnsafeGetItem, which is the same question without the tolerance.
    public static bool UnsafeGetItem(Mod mod, string name, Item item) =>
        item.type == Ids.GetOrAdd((mod, name), ResolveId);

    private static int ResolveId((Mod Mod, string Name) key)
    {
        if (Array.IndexOf(_tolerant, key.Mod) >= 0)
            return key.Mod.TryFind(key.Name, out ModItem found) ? found.Type : -1;

        // Not a tolerant mod, so a wrong name is an exception in the original too. Letting Find
        // raise it keeps a broken call site broken rather than quietly rebalancing nothing.
        return key.Mod.Find<ModItem>(key.Name).Type;
    }

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod infernalEclipse))
            return;

        Type owner = infernalEclipse.Code.GetType(TargetType);
        _tolerant = Array.ConvertAll(TolerantMods,
            name => ModLoader.TryGetMod(name, out Mod found) ? found : null);

        // A reload hands out new ids and new Mod instances, and this type outlives neither, so the
        // table has to start empty or it would answer the previous load's questions.
        Ids.Clear();

        Replace(owner, "GetItem", nameof(GetItem));
        Replace(owner, "UnsafeGetItem", nameof(UnsafeGetItem));
    }

    // The two helpers are independent: one that cannot be replaced leaves the mod's own version in
    // place rather than taking the other down with it.
    private void Replace(Type owner, string methodName, string replacementName)
    {
        MethodInfo target = owner?.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public
            | BindingFlags.DeclaredOnly, binder: null,
            [typeof(Mod), typeof(string), typeof(Item)], modifiers: null);

        if (target?.ReturnType != typeof(bool))
        {
            Mod.Logger.Error($"Infernal item balance cache: {TargetType}.{methodName} is not a " +
                "static bool method taking (Mod, string, Item), left as it is");
            return;
        }

        MethodInfo replacement = typeof(InfernalItemBalanceCache)
            .GetMethod(replacementName, BindingFlags.Static | BindingFlags.Public);

        MonoModHooks.Modify(target, il =>
        {
            ILCursor cursor = new ILCursor(il);
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.Emit(OpCodes.Ldarg_1);
            cursor.Emit(OpCodes.Ldarg_2);
            cursor.Emit(OpCodes.Call, replacement);
            cursor.Emit(OpCodes.Ret);
        });
    }
}
