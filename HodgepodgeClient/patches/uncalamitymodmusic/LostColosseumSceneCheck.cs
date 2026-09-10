using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// LostColosseum_Aftermath is a ModSceneEffect, so IsSceneEffectActive runs once per tick. It opens
// by allocating an NPC and calling SetDefaults(BereftVassal) on it -- which runs that ModNPC's
// SetDefaults and every GlobalNPC.SetDefaults in the pack -- purely to hand the instance to
// GetKillCount, which reads one bestiary id off it. The NPC is then discarded. Across a ten minute
// capture this single method accounted for 98% of every NPC.SetDefaults call in the game.
//
// The method can only ever return MusicFlags.LostColosseum, a static bool derived once per tick
// from player.InModBiome(LostColosseumBiome), and it tests that term last. Testing it first is
// exactly equivalent: with the flag false the original returns false down both paths, and
// everything in between only reads game state.
public class LostColosseumSceneCheck : Patch
{
    private const string TargetMod = "UnCalamityModMusic";
    private const string SceneEffectTypeName =
        "UnCalamityModMusic.Common.ModCompatibility.LostColosseum_Aftermath";
    private const string FlagsTypeName = "UnCalamityModMusic.Common.MusicFlags";
    private const string FlagName = "LostColosseum";

    private static FieldInfo _lostColosseumField;

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().LostColosseumSceneCheck;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod unCalamityMusic))
            return;

        MethodInfo isSceneEffectActive = unCalamityMusic.Code.GetType(SceneEffectTypeName)
            ?.GetMethod("IsSceneEffectActive",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        _lostColosseumField = unCalamityMusic.Code.GetType(FlagsTypeName)
            ?.GetField(FlagName, BindingFlags.Static | BindingFlags.NonPublic);

        if (isSceneEffectActive?.ReturnType != typeof(bool)
            || _lostColosseumField?.FieldType != typeof(bool))
        {
            Mod.Logger.Error("Lost colosseum scene check: a bool " +
                $"{SceneEffectTypeName}.IsSceneEffectActive or a static bool {FlagsTypeName}." +
                $"{FlagName} is missing, patch disabled");
            return;
        }

        // The equivalence argument rests entirely on the flag still being the only thing that can
        // make this method true. If it stops reading the flag, an early false would silence a
        // working scene effect rather than skip dead work.
        if (!ReadsField(isSceneEffectActive, _lostColosseumField))
        {
            Mod.Logger.Error($"Lost colosseum scene check: IsSceneEffectActive no longer reads " +
                $"{FlagName}, so returning false early would change behaviour, patch disabled");
            return;
        }

        MonoModHooks.Modify(isSceneEffectActive, SkipUnlessInsideColosseum);
    }

    private void SkipUnlessInsideColosseum(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);
        ILLabel insideColosseum = cursor.DefineLabel();
        cursor.Emit(OpCodes.Ldsfld, _lostColosseumField);
        cursor.Emit(OpCodes.Brtrue, insideColosseum);
        cursor.Emit(OpCodes.Ldc_I4_0);
        cursor.Emit(OpCodes.Ret);
        cursor.MarkLabel(insideColosseum);
    }
}
