using System.Reflection;
using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// The lingering damage fields ask for a particle every tick wherever they are, and the arena holds
// far more of them than fit on screen. A particle spawned off screen is scattered around a field
// nobody is looking at, retires on its own, and is never drawn -- but it still occupies a pool slot
// for its whole lifetime, and vanilla's ParticlePool searches for a free slot by walking the pool
// from the front. Off screen fields are what make that walk long.
//
// Skipping those spawns costs nothing visible. It divides the pool pressure rather than removing
// the walk, which is ParticlePoolScanCursor's job; the two are worth having separately because this
// one carries no risk to anything outside StarsAbove.
//
// This only applies while LingeringFieldParticleBroadcast is on. With the spawn still broadcast,
// answering for the local screen would decide what every other player sees as well, and a field off
// this screen is usually on someone else's.
public class LingeringFieldParticleVisibility : Patch
{
    private const string TargetMod = "StarsAbove";

    // The three fields scatter their particle up to 250 pixels from the projectile centre, so the
    // screen rectangle is padded past that before anything is called invisible.
    private const float SpawnSpread = 300f;

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().LingeringFieldParticleVisibility;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod starsAbove))
            return;

        if (!ModContent.GetInstance<ClientConfig>().LingeringFieldParticleBroadcast)
        {
            Mod.Logger.Error("Lingering field particle visibility: disabled, it needs lingering " +
                "field particle broadcast on or it would decide what other players see");
            return;
        }

        foreach (string type in LingeringFieldSpawns.TargetTypes)
        {
            MethodInfo ai = LingeringFieldSpawns.FindAI(starsAbove, type);
            if (ai == null)
            {
                Mod.Logger.Error($"Lingering field particle visibility: {type}.AI is missing, " +
                    "that field left as it is");
                continue;
            }

            MonoModHooks.Modify(ai, il => GateOnScreen(il, type));
        }
    }

    // Wraps the spawn in a visibility test by branching from the start of its argument list to just
    // past the call. Nothing else in the AI is touched, so the field keeps its light, its dust and
    // its hitbox whether or not anyone is looking at it.
    private void GateOnScreen(ILContext il, string type)
    {
        ILCursor cursor = new(il);
        int gated = 0;

        foreach (Instruction instruction in new System.Collections.Generic.List<Instruction>(
            il.Body.Instructions))
        {
            if (!LingeringFieldSpawns.IsParticleSpawn(instruction))
                continue;

            Instruction flag = LingeringFieldSpawns.FindClientOnlyArgument(instruction);
            if (flag == null)
            {
                Mod.Logger.Error($"Lingering field particle visibility: {type}.AI no longer " +
                    "passes a constant for clientOnly, that spawn left as it is");
                continue;
            }

            ILLabel skip = il.DefineLabel();
            cursor.Goto(flag, MoveType.Before);
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.Emit(OpCodes.Call, typeof(LingeringFieldParticleVisibility)
                .GetMethod(nameof(IsWorthSpawning), BindingFlags.Static | BindingFlags.Public));
            cursor.Emit(OpCodes.Brfalse, skip);

            cursor.Goto(instruction, MoveType.After);
            cursor.MarkLabel(skip);
            gated++;
        }

        if (gated == 0)
            Mod.Logger.Error($"Lingering field particle visibility: {type}.AI no longer spawns a " +
                "particle, left as it is");
        else
            Mod.Logger.Info($"Lingering field particle visibility: {type}.AI now skips its " +
                $"{gated} spawn off screen");
    }

    // Public because the patched IL calls it. Answers for the projectile that owns the AI frame.
    public static bool IsWorthSpawning(ModProjectile modProjectile)
    {
        Vector2 center = modProjectile.Projectile.Center;
        Vector2 screen = Main.screenPosition;

        return center.X > screen.X - SpawnSpread
            && center.X < screen.X + Main.screenWidth + SpawnSpread
            && center.Y > screen.Y - SpawnSpread
            && center.Y < screen.Y + Main.screenHeight + SpawnSpread;
    }
}
