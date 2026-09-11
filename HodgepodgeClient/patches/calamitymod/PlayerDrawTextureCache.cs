using System.Collections.Generic;
using System.Reflection;
using MonoMod.Cil;
using Terraria.DataStructures;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// Two Calamity player draw paths ask the asset repository for fixed textures by path, once per
// player per frame. TailDraw.Draw requests its two tail sprites before it knows whether the player
// has a tail at all:
//
//     Texture2D value = ModContent.Request<Texture2D>("CalamityMod/Items/Accessories/Wings/TiredTailSegment", ImmediateLoad).Value;
//     Texture2D value2 = ModContent.Request<Texture2D>("CalamityMod/Items/Accessories/Wings/TiredTailTail", ImmediateLoad).Value;
//     if (modPlayer.tailPos == null)
//         return;
//
// and CalamityPlayer.ModifyDrawInfo builds a list of seventeen backpack textures and a matching
// list of seventeen item ids, every one resolved through ModContent, before testing whether the
// held item is any of them. On the lower-end client the two requests were 0.20 and 0.17 ms/frame.
//
// The requests are answered from a table and the item ids fold to their constants. The lists are
// still built each frame; what they are built from no longer costs a lookup.
//
// Read against CalamityMod 2.2.4.
public class PlayerDrawTextureCache : Patch
{
    private const string TargetMod = "CalamityMod";
    private const string TailType = "CalamityMod.Items.Accessories.Wings.TailDraw";
    private const string PlayerType = "CalamityMod.CalPlayer.CalamityPlayer";

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().PlayerDrawTextureCache;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod calamity))
            return;

        MethodInfo tailDraw = calamity.Code.GetType(TailType)?.GetMethod("Draw",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, binder: null,
            [typeof(PlayerDrawSet).MakeByRefType()], modifiers: null);
        MethodInfo modifyDrawInfo = calamity.Code.GetType(PlayerType)?.GetMethod("ModifyDrawInfo",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, binder: null,
            [typeof(PlayerDrawSet).MakeByRefType()], modifiers: null);

        if (tailDraw == null || modifyDrawInfo == null)
        {
            Mod.Logger.Error($"Player draw texture cache: {TailType}.Draw or {PlayerType}." +
                "ModifyDrawInfo is missing, patch disabled");
            return;
        }

        MonoModHooks.Modify(tailDraw, il => CacheTextures(il, "TailDraw.Draw"));
        MonoModHooks.Modify(modifyDrawInfo, il =>
        {
            CacheTextures(il, "ModifyDrawInfo");

            List<string> skipped = [];
            int folded = ContentIdFolding.FoldContentLookups(il,
                ContentIdFolding.InAssembly(calamity.Code), skipped.Add);
            ContentIdFolding.Report(Mod, "Player draw texture cache", "ModifyDrawInfo", folded,
                skipped);
        });
    }

    private void CacheTextures(ILContext il, string method)
    {
        if (TextureRequestCache.RewriteRequests(il) == 0)
            Mod.Logger.Error($"Player draw texture cache: {method} no longer requests a texture " +
                "by name, left as it is");
    }
}
