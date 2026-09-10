using System;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// MusicUtilities.CalamityMusicEvent is called from MusicFlags.PreUpdate, so once per tick, and it
// is written entirely in reflection:
//
//     if (ModLoader.TryGetMod("CalamityMod", out var result))
//     {
//         Type type = result.GetType().Assembly.GetType("CalamityMod.Systems.MusicEventSystem");
//         if (type != null)
//             return type.GetProperty("TrackStart", ...).GetValue(null) as DateTime?;
//     }
//
// Every tick pays an Assembly.GetType name lookup across the whole of Calamity, a GetProperty
// lookup and a reflective invoke, to read one static property. It is the largest single piece of
// MusicFlags.PreUpdate, and none of those handles change once mods have loaded.
//
// This is the same defect ExoMechSkyLookup removes from InfernumModeMusic, in a different mod.
public class MusicEventTrackLookup : Patch
{
    private const string TargetMod = "UnCalamityModMusic";
    private const string TargetType = "UnCalamityModMusic.Common.MusicUtilities";
    private const string EventSystemTypeName = "CalamityMod.Systems.MusicEventSystem";
    private const string TrackProperty = "TrackStart";

    private static Func<DateTime?> _trackStart;

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().MusicEventTrackLookup;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod unCalamityMusic)
            || !ModLoader.TryGetMod("CalamityMod", out Mod calamity))
            return;

        MethodInfo musicEvent = unCalamityMusic.Code.GetType(TargetType)?.GetMethod(
            "CalamityMusicEvent", BindingFlags.Static | BindingFlags.Public, binder: null,
            Type.EmptyTypes, modifiers: null);
        MethodInfo trackStart = calamity.Code.GetType(EventSystemTypeName)?.GetProperty(
            TrackProperty, BindingFlags.Static | BindingFlags.Public)?.GetMethod;

        if (musicEvent?.ReturnType != typeof(DateTime?) || trackStart == null)
        {
            Mod.Logger.Error($"Music event track lookup: {TargetType}.CalamityMusicEvent or " +
                $"{EventSystemTypeName}.{TrackProperty} is missing, patch disabled");
            return;
        }

        // The mod reads the property through `as DateTime?`, which accepts either shape, so the
        // delegate has to be built against whichever one Calamity actually declares.
        if (trackStart.ReturnType == typeof(DateTime?))
            _trackStart = trackStart.CreateDelegate<Func<DateTime?>>();
        else if (trackStart.ReturnType == typeof(DateTime))
        {
            Func<DateTime> exact = trackStart.CreateDelegate<Func<DateTime>>();
            _trackStart = () => exact();
        }
        else
        {
            Mod.Logger.Error($"Music event track lookup: {TrackProperty} is a " +
                $"{trackStart.ReturnType.Name}, not a DateTime, patch disabled");
            return;
        }

        MonoModHooks.Modify(musicEvent, AnswerFromCachedProperty);
    }

    // Short circuits the method rather than replacing its body, leaving the mod's own code behind
    // as dead code instead of this patch's idea of what it used to say.
    private void AnswerFromCachedProperty(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);

        // A cached handle is only the same answer while the method is still looking up this exact
        // property. Both names have to appear, in the order the reflection uses them.
        if (!cursor.TryGotoNext(i => i.MatchLdstr(EventSystemTypeName))
            || !cursor.TryGotoNext(i => i.MatchLdstr(TrackProperty)))
        {
            Mod.Logger.Error($"Music event track lookup: CalamityMusicEvent no longer resolves " +
                $"{EventSystemTypeName}.{TrackProperty} by name, patch disabled");
            return;
        }

        cursor.Index = 0;
        cursor.EmitDelegate<Func<DateTime?>>(ReadTrackStart);
        cursor.Emit(OpCodes.Ret);
    }

    private static DateTime? ReadTrackStart() => _trackStart();
}
