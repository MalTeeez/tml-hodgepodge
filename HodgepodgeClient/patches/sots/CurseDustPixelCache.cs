using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// PharaohsCurse.SpawnPassiveDust finds the opaque pixels of a sprite by reading it back off the
// GPU, then spawns dust on the ones whose alpha is full:
//
//     Color[] array = new Color[width * height];
//     texture.GetData<Color>(array);
//
// PreAI calls it up to five times a tick while the boss is alive, so that is five whole-texture
// GPU to CPU readbacks per tick of textures that are immutable mod assets. A readback also stalls
// the pipeline, and that part lands outside managed frames, so what a CPU sampler attributes here
// is a floor rather than the whole cost.
//
// The pixels never change, so they are read once per texture and copied out of a cache after that.
// The mod's own loop is untouched: it still walks the array it allocated and still reads nothing
// from it but each pixel's alpha.
public class CurseDustPixelCache : Patch
{
    private const string TargetMod = "SOTS";
    private const string TargetType = "SOTS.NPCs.Boss.Curse.PharaohsCurse";
    private const string TargetMethod = "SpawnPassiveDust";

    // Weakly keyed so the pixels do not outlive a texture dropped on a mod reload.
    private static readonly ConditionalWeakTable<Texture2D, Color[]> Pixels = new();

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().CurseDustPixelCache;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod sots))
            return;

        MethodInfo spawnDust = sots.Code.GetType(TargetType)?.GetMethod(TargetMethod,
            BindingFlags.Static | BindingFlags.Public);

        if (spawnDust == null)
        {
            Mod.Logger.Error($"Curse dust pixel cache: {TargetType}.{TargetMethod} is missing, " +
                "patch disabled");
            return;
        }

        MonoModHooks.Modify(spawnDust, ReadThePixelsOnce);
    }

    // Rewrites the readback alone rather than the allocation in front of it, so the array the mod
    // made still arrives holding the pixels it asked for and nothing downstream can tell.
    private void ReadThePixelsOnce(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.Before, instruction =>
                instruction.MatchCallvirt(out MethodReference called) && called.Name == "GetData"
                && called.Parameters.Count == 1
                && called.DeclaringType.Name == nameof(Texture2D)))
        {
            Mod.Logger.Error($"Curse dust pixel cache: {TargetMethod} no longer reads a texture " +
                "back off the GPU, patch disabled");
            return;
        }

        cursor.Next.OpCode = OpCodes.Call;
        cursor.Next.Operand = il.Import(
            typeof(CurseDustPixelCache).GetMethod(nameof(CopyCachedPixels)));
    }

    // Stands in for Texture2D.GetData<Color>(Color[]) and so takes the same two values off the
    // stack in the same order.
    public static void CopyCachedPixels(Texture2D texture, Color[] destination)
    {
        Color[] cached = Pixels.GetValue(texture, ReadPixels);
        Array.Copy(cached, destination, Math.Min(cached.Length, destination.Length));
    }

    private static Color[] ReadPixels(Texture2D texture)
    {
        Color[] pixels = new Color[texture.Width * texture.Height];
        texture.GetData(pixels);
        return pixels;
    }
}
