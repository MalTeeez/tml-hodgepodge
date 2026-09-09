using System.Reflection;
using Mono.Cecil;
using MonoMod.Cil;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// Calamity steps its foreground effects from a draw hook and from an update hook, so on a client
// every particle is spawned and advanced twice per tick. Drop the call in the draw hook: the
// remaining one runs on the game tick, which keeps the effect identical at any frame rate.
public class ForegroundDoubleUpdate : Patch
{
    private const string ForegroundTypeName =
        "CalamityMod.ForegroundDrawing.LoopingTextures.LoopingTextureForeground";

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().ForegroundDoubleUpdate;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod("CalamityMod", out Mod calamity))
            return;

        MethodInfo postDrawTiles = calamity.Code.GetType(ForegroundTypeName)
            ?.GetMethod("PostDrawTiles", BindingFlags.Instance | BindingFlags.Public);

        if (postDrawTiles == null)
        {
            Mod.Logger.Error($"Foreground double update: {ForegroundTypeName}.PostDrawTiles " +
                "not found, patch disabled");
            return;
        }

        MonoModHooks.Modify(postDrawTiles, RemoveDrawPathUpdate);
    }

    private void RemoveDrawPathUpdate(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.Before,
                i => i.MatchLdarg(0),
                i => i.MatchCallvirt(out MethodReference called) && called.Name == "Update"))
        {
            Mod.Logger.Error("Foreground double update: Update() call not found in " +
                "PostDrawTiles, patch disabled");
            return;
        }

        cursor.RemoveRange(2);
    }
}
