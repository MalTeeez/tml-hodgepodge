using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework.Graphics;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using ReLogic.Content;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// Several mods in this pack ask for a texture by path on a per entity, per frame draw path:
//
//     ModContent.Request<Texture2D>("CalamityMod/Particles/BloomCircle", AssetRequestMode.ImmediateLoad).Value
//
// Each request splits the path, finds the mod by name, takes the repository's lock and looks the
// rest of the path up in its table, to hand back the asset it handed back last frame. The stand-in
// answers from a table of its own keyed on the path, which is unique across mods because every
// path a ModContent request accepts starts with the mod's name.
//
// The table only answers with a loaded asset. A caller passing ImmediateLoad is owed a loaded
// value, and the repository is what makes that true, so a still loading asset is asked for again.
public class TextureRequestCache : ModSystem
{
    private static readonly Dictionary<string, Asset<Texture2D>> Textures = [];

    public override void Unload() => Textures.Clear();

    // Rewrites every ModContent.Request<Texture2D>(string, AssetRequestMode) call in a method to
    // go through the table, and says how many it found. None means the method no longer looks the
    // way the calling patch was written against.
    public static int RewriteRequests(ILContext il)
    {
        MethodInfo cached = typeof(TextureRequestCache).GetMethod(nameof(Request));
        int rewritten = 0;

        foreach (Instruction instruction in il.Body.Instructions)
        {
            if (!IsTextureRequest(instruction))
                continue;

            instruction.OpCode = OpCodes.Call;
            instruction.Operand = il.Import(cached);
            rewritten++;
        }

        return rewritten;
    }

    // The texture argument is part of the match rather than an assumption about it: the stand-in
    // returns an Asset<Texture2D>, so rewriting a request for anything else would leave the wrong
    // type on the stack, which is a crash rather than a patch that switches itself off.
    private static bool IsTextureRequest(Instruction instruction) =>
        instruction.MatchCall(out MethodReference called)
        && called.Name == nameof(ModContent.Request) && called.Parameters.Count == 2
        && called.DeclaringType.Name == nameof(ModContent)
        && called is GenericInstanceMethod request
        && request.GenericArguments[0].Name == nameof(Texture2D);

    // Stands in for ModContent.Request<Texture2D>(string, AssetRequestMode) and so takes the same
    // two values off the stack in the same order.
    public static Asset<Texture2D> Request(string path, AssetRequestMode mode)
    {
        if (Textures.TryGetValue(path, out Asset<Texture2D> texture) && texture.IsLoaded)
            return texture;

        texture = ModContent.Request<Texture2D>(path, mode);
        Textures[path] = texture;
        return texture;
    }
}
