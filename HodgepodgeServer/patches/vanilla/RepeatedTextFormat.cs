using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Terraria.Localization;
using Terraria.ModLoader;

namespace HodgepodgeServer;

// Calamity armor sets assign their set bonus text from inside UpdateArmorSet:
//
//     player.setBonus = this.GetLocalization("SetBonus").Format(SetBonusRogueStealth.ToStealth(),
//         SetBonusPoisonDuration.FramesToSeconds());
//
// UpdateArmorSet runs once per wearing player per tick, and any set bonus text carrying a plural
// pattern sends LocalizedText.Format through a regex replace on every one of those calls. On the
// profiled server one worn set was 0.071 ms of a 9.02 ms tick, and 71% of everything
// Player.UpdateArmorSets cost. Fifty six Calamity sets are written this way.
//
// Rewriting each of those call sites would mean fifty six IL patches against another mod. Caching
// the result of the shared method instead is one patch, keeps the text correct rather than
// blanking a field the server happens not to read, and helps every other caller that formats the
// same text with the same arguments each tick.
//
// The cache is keyed on the LocalizedText itself and holds the value it was built from, so a
// language or resource pack change -- which replaces that string -- misses and rebuilds.
public class RepeatedTextFormat : Patch
{
    private delegate string OrigFormat(LocalizedText text, object[] args);

    private static readonly ConditionalWeakTable<LocalizedText, Memo> Memos = new();

    protected override bool Enabled => ModContent.GetInstance<ServerConfig>().RepeatedTextFormat;

    protected override void Apply()
    {
        MethodInfo format = typeof(LocalizedText).GetMethod(nameof(LocalizedText.Format),
            BindingFlags.Instance | BindingFlags.Public, binder: null, [typeof(object[])],
            modifiers: null);

        if (format == null || format.ReturnType != typeof(string))
        {
            Mod.Logger.Error("Repeated text format: LocalizedText.Format(object[]) is gone, " +
                "patch disabled");
            return;
        }

        MonoModHooks.Add(format, (Func<OrigFormat, LocalizedText, object[], string>)FormatOnce);
    }

    private static string FormatOnce(OrigFormat orig, LocalizedText text, object[] args)
    {
        if (!Cacheable(args))
            return orig(text, args);

        string value = text.Value;
        if (Memos.TryGetValue(text, out Memo memo) && ReferenceEquals(memo.Value, value)
            && SameArguments(memo.Arguments, args))
            return memo.Result;

        // Built before it is published, and published in one reference assignment, so a call from
        // another thread either sees the old entry or the new one and never a half built one.
        Memo rebuilt = new Memo(value, (object[])args.Clone(), orig(text, args));
        Memos.AddOrUpdate(text, rebuilt);
        return rebuilt.Result;
    }

    // Only arguments whose text is fixed by their value are safe to remember. A live object would
    // keep comparing equal to itself while the string it formats into changed underneath, so
    // anything else is handed straight to the original.
    private static bool Cacheable(object[] args)
    {
        foreach (object argument in args)
        {
            if (argument != null && argument is not string && !argument.GetType().IsPrimitive)
                return false;
        }

        return true;
    }

    private static bool SameArguments(object[] remembered, object[] args)
    {
        if (remembered.Length != args.Length)
            return false;

        for (int index = 0; index < args.Length; index++)
        {
            if (!Equals(remembered[index], args[index]))
                return false;
        }

        return true;
    }

    private sealed record Memo(string Value, object[] Arguments, string Result);
}
