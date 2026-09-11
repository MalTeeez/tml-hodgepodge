using System;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeServer;

// BlueMoonsPlayer.PostUpdateBuffs runs once per player per tick and calls two methods that are
// written entirely in reflection, each of them rebuilding every handle it needs from scratch:
//
//     Type type = InfernalCrossmod.BlueMoon.Mod.Code.GetType("BlueMoon.Buffs.FloralBlessingPlayer");
//     MethodInfo method = typeof(Player).GetMethod("GetModPlayer", Type.EmptyTypes);
//     object obj = method.MakeGenericMethod(type).Invoke(base.Player, null);
//     FieldInfo field = type.GetField("hasFloralBlessing", ...);
//     if (!(field == null) && (bool)field.GetValue(obj)) base.Player.lifeRegen += num;
//
// The name lookup alone walks the whole BlueMoon assembly, and on the profiled server the two
// together were 0.329 ms of a 9.02 ms tick, most of it inside Assembly.GetType.
//
// The buffs are real gameplay -- life regen and damage on a server authoritative player -- so this
// patch changes how the values are read rather than whether they are read. Every handle is
// resolved once at load and the effects are applied as the mod applies them.
//
// The client mod carries the same patch for the same two methods; Patch only applies this one on a
// dedicated server, so the two never rewrite the same process.
//
// A body written in reflection cannot be pinned by inspecting its IL the way the other patches
// pin theirs, so if InfernalEclipseAPI changes what these buffs do, this patch keeps applying the
// old effect until it is updated. Read against InfernalEclipseAPI 2026.7.
public class BlueMoonBuffLookup : Patch
{
    private const string TargetMod = "InfernalEclipseAPI";
    private const string TargetType = "InfernalEclipseAPI.Core.Players.BlueMoonsPlayer";
    private const string BlueMoonMod = "BlueMoon";

    private static BuffProbe _floralBlessing;
    private static BuffProbe _mintyFreshness;

    protected override bool Enabled => ModContent.GetInstance<ServerConfig>().BlueMoonBuffLookup;

    public static void FloralBlessing(ModPlayer self)
    {
        Player player = self.Player;
        if (_floralBlessing.IsSet(player))
            player.lifeRegen += Main.hardMode ? 1 : 2;
    }

    public static void MintyFreshness(ModPlayer self)
    {
        Player player = self.Player;
        if (!_mintyFreshness.IsSet(player))
            return;

        player.moveSpeed -= 0.13f;
        player.GetDamage(DamageClass.Melee) -= 0.2f;
        player.GetDamage(DamageClass.Ranged) -= 0.2f;
        player.GetDamage(DamageClass.Magic) -= 0.2f;
        player.GetDamage(DamageClass.Summon) -= 0.2f;
        player.GetDamage(DamageClass.Generic) += 0.07f;
    }

    protected override void Apply()
    {
        // BlueMoon absent means BlueMoonsPlayer never loads, so there is nothing to make cheaper.
        if (!ModLoader.TryGetMod(TargetMod, out Mod infernalEclipse)
            || !ModLoader.TryGetMod(BlueMoonMod, out Mod blueMoon))
            return;

        Type owner = infernalEclipse.Code.GetType(TargetType);

        _floralBlessing = BuffProbe.Create(blueMoon, "BlueMoon.Buffs.FloralBlessingPlayer",
            "hasFloralBlessing");
        _mintyFreshness = BuffProbe.Create(blueMoon, "BlueMoon.Buffs.MintyFreshnessPlayer",
            "hasMintyFreshness");

        Replace(owner, "UpdateFloralBlessing", _floralBlessing, nameof(FloralBlessing));
        Replace(owner, "UpdateMintyFreshness", _mintyFreshness, nameof(MintyFreshness));
    }

    // The two buffs are independent, so a probe that cannot be built leaves its own method on the
    // mod's original reflection rather than taking the other one down with it.
    private void Replace(Type owner, string methodName, BuffProbe probe, string replacementName)
    {
        MethodInfo target = owner?.GetMethod(methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
            binder: null, Type.EmptyTypes, modifiers: null);

        if (probe == null || target == null || target.ReturnType != typeof(void))
        {
            Mod.Logger.Error($"Blue moon buff lookup: {TargetType}.{methodName} is not a declared " +
                "parameterless void method, or the BlueMoon field it reads is gone, " +
                $"{methodName} left as it is");
            return;
        }

        MethodInfo replacement = typeof(BlueMoonBuffLookup).GetMethod(replacementName,
            BindingFlags.Static | BindingFlags.Public);

        MonoModHooks.Modify(target, il =>
        {
            ILCursor cursor = new ILCursor(il);
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.Emit(OpCodes.Call, replacement);
            cursor.Emit(OpCodes.Ret);
        });
    }

    // The three handles the mod rebuilds every tick: the BlueMoon ModPlayer type, the accessor
    // that fetches it off a Player, and the bool field on it that says whether the buff is on.
    private sealed class BuffProbe
    {
        private readonly Func<Player, ModPlayer> _fetch;
        private readonly FieldInfo _flag;

        private BuffProbe(Func<Player, ModPlayer> fetch, FieldInfo flag)
        {
            _fetch = fetch;
            _flag = flag;
        }

        public static BuffProbe Create(Mod blueMoon, string typeName, string fieldName)
        {
            Type modPlayer = blueMoon.Code.GetType(typeName);
            FieldInfo flag = modPlayer?.GetField(fieldName,
                BindingFlags.Instance | BindingFlags.Public);

            if (flag?.FieldType != typeof(bool) || !typeof(ModPlayer).IsAssignableFrom(modPlayer))
                return null;

            // An open delegate over Player.GetModPlayer<T>(): the instance arrives as the first
            // argument, and T being a ModPlayer subclass makes the return type compatible.
            MethodInfo fetch = typeof(Player)
                .GetMethod(nameof(Player.GetModPlayer), Type.EmptyTypes)
                .MakeGenericMethod(modPlayer);

            return new BuffProbe(fetch.CreateDelegate<Func<Player, ModPlayer>>(), flag);
        }

        public bool IsSet(Player player)
        {
            ModPlayer modPlayer = _fetch(player);
            return modPlayer != null && (bool)_flag.GetValue(modPlayer);
        }
    }
}
