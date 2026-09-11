using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework.Graphics;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using ReLogic.Content;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// LootBeamItem.PreDrawInWorld asks for its beam and glow textures by path, on every item drawing a
// beam, on every frame:
//
//     Texture2D value4 = base.Mod.Assets.Request<Texture2D>("Beams/SimpleBeam", ImmediateLoad).Value;
//
// Six such paths across the additive and non-additive branches, three of them reached per item per
// frame. Each one takes the repository's lock and looks the name up in its table to hand back the
// same asset it handed back last frame. On the profiled client that was 0.65 ms/frame of the
// 0.70 ms/frame the whole method cost, so the drawing is nearly free and the naming is not.
//
// Unlike the curse icons this work is wanted -- the beams are drawn -- so the patch changes how the
// asset is found rather than whether it is. The lookup is answered from a small table the first
// time each path is seen and the repository is asked once per path per load.
//
// Keyed on the path alone. Every call in this method reaches the repository through the mod's own
// Assets, and a mod's assets come from its .tmod rather than from the resource pack stack, so there
// is no second repository to confuse it with and nothing swaps the asset out underneath.
public class LootBeamTextureCache : Patch
{
    private const string TargetMod = "LootBeams";
    private const string TargetType = "LootBeams.LootBeamItem";
    private const string TargetMethod = "PreDrawInWorld";

    private static readonly Dictionary<string, Asset<Texture2D>> Textures = [];

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().LootBeamTextureCache;

    public override void Unload() => Textures.Clear();

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod lootBeams))
            return;

        MethodInfo preDraw = lootBeams.Code.GetType(TargetType)?.GetMethod(TargetMethod,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

        if (preDraw == null)
        {
            Mod.Logger.Error($"Loot beam texture cache: {TargetType}.{TargetMethod} is missing, " +
                "patch disabled");
            return;
        }

        MonoModHooks.Modify(preDraw, CacheEachPath);
    }

    // Rewrites the call rather than the paths in front of it, so a LootBeams release that renames a
    // texture or adds a fourth one is carried along instead of being pinned to the six paths there
    // are today.
    private void CacheEachPath(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);
        MethodInfo cached = typeof(LootBeamTextureCache).GetMethod(nameof(Request));
        int rewritten = 0;

        while (cursor.TryGotoNext(MoveType.Before, IsRepositoryRequest))
        {
            cursor.Next.OpCode = OpCodes.Call;
            cursor.Next.Operand = il.Import(cached);
            cursor.Index++;
            rewritten++;
        }

        if (rewritten == 0)
            Mod.Logger.Error($"Loot beam texture cache: {TargetMethod} no longer requests a " +
                "texture by name, patch disabled");
    }

    // The texture argument is part of the match rather than an assumption about it: the stand-in
    // returns an Asset<Texture2D>, so rewriting a request for anything else would leave the wrong
    // type on the stack, which is a crash rather than a patch that switches itself off.
    private static bool IsRepositoryRequest(Instruction instruction) =>
        instruction.MatchCallvirt(out MethodReference called)
        && called.Name == nameof(AssetRepository.Request) && called.Parameters.Count == 2
        && called.DeclaringType.Name == nameof(AssetRepository)
        && called is GenericInstanceMethod request
        && request.GenericArguments[0].Name == nameof(Texture2D);

    // Stands in for AssetRepository.Request<Texture2D>(string, AssetRequestMode) and so takes the
    // same three values off the stack in the same order.
    public static Asset<Texture2D> Request(AssetRepository repository, string path,
        AssetRequestMode mode)
    {
        if (Textures.TryGetValue(path, out Asset<Texture2D> texture))
            return texture;

        texture = repository.Request<Texture2D>(path, mode);
        Textures[path] = texture;
        return texture;
    }
}
