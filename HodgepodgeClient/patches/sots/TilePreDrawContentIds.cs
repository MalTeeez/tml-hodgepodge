using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework.Graphics;
using MonoMod.Cil;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// SOTSTile.PreDraw is a GlobalTile hook, so it runs for every tile drawn, every frame. It opens
// with a chain of comparisons shaped like this, one pair per dissolving tile:
//
//     if (tile.WallType == ModContent.WallType<NatureWallWall>()
//         && tile.TileType != ModContent.TileType<DissolvingNatureTile>())
//
// -- sixteen or more ModContent lookups per tile, for numbers that are handed out once when
// content is registered and never change afterwards. Measured at 0.032 ms/frame just resolving
// them, inside a method costing 0.068 ms/frame of its own work.
//
// Every one of those calls is replaced with the id it returns, resolved once here.
public class TilePreDrawContentIds : Patch
{
    private const string TargetMod = "SOTS";
    private const string TargetType = "SOTS.SOTSTile";

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().TilePreDrawContentIds;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod sots))
            return;

        MethodInfo preDraw = sots.Code.GetType(TargetType)?.GetMethod("PreDraw",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, binder: null,
            [typeof(int), typeof(int), typeof(int), typeof(SpriteBatch)], modifiers: null);

        if (preDraw == null)
        {
            Mod.Logger.Error($"Tile pre draw content ids: {TargetType}.PreDraw is missing, " +
                "patch disabled");
            return;
        }

        MonoModHooks.Modify(preDraw, il =>
        {
            List<string> skipped = [];
            int folded = ContentIdFolding.FoldContentLookups(il,
                ContentIdFolding.InAssembly(sots.Code), skipped.Add);

            ContentIdFolding.Report(Mod, "Tile pre draw content ids", "PreDraw", folded, skipped);
        });
    }
}
