using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// The three lingering damage fields spawn their particle with `clientOnly: false`, which sends it
// through the network rather than spawning it. Vanilla routes that through SendToServerAndSelf, so
// the calling client pays a serialize, a send and a loopback deserialize, the server relays, and
// every other client deserializes and spawns it too. Projectile AI already runs on every client,
// so each client also raises its own copy: the same particle is drawn once per player.
//
// Flipping the flag to true spawns the particle directly instead. Every client still raises the one
// it always raised locally, so the effect keeps the density its author wrote; what stops is the
// duplication and the traffic.
//
// Measured: the receive path fell from 44.06% of the client thread to 0.46%. That is traffic, not
// frame rate -- what those packets were spending their time in is the pool scan underneath, which
// this patch does not touch and which runs the same either way. See ParticlePoolScanCursor.
public class LingeringFieldParticleBroadcast : Patch
{
    private const string TargetMod = "StarsAbove";

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().LingeringFieldParticleBroadcast;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod starsAbove))
            return;

        foreach (string type in LingeringFieldSpawns.TargetTypes)
        {
            System.Reflection.MethodInfo ai = LingeringFieldSpawns.FindAI(starsAbove, type);
            if (ai == null)
            {
                Mod.Logger.Error($"Lingering field particle broadcast: {type}.AI is missing, " +
                    "that field left as it is");
                continue;
            }

            MonoModHooks.Modify(ai, il => SpawnLocally(il, type));
        }
    }

    private void SpawnLocally(ILContext il, string type)
    {
        int patched = 0;
        foreach (Instruction instruction in il.Body.Instructions)
        {
            if (!LingeringFieldSpawns.IsParticleSpawn(instruction))
                continue;

            Instruction flag = LingeringFieldSpawns.FindClientOnlyArgument(instruction,
                clientOnly: false);
            if (flag == null)
            {
                Mod.Logger.Error($"Lingering field particle broadcast: {type}.AI no longer " +
                    "passes a constant for clientOnly, that spawn left as it is");
                continue;
            }

            flag.OpCode = OpCodes.Ldc_I4_1;
            patched++;
        }

        // No match means the AI has been rewritten into something this patch has not read, so its
        // IL is handed back untouched rather than guessed at.
        if (patched == 0)
            Mod.Logger.Error($"Lingering field particle broadcast: {type}.AI no longer " +
                "broadcasts a particle spawn, left as it is");
        else
            Mod.Logger.Info($"Lingering field particle broadcast: {type}.AI now spawns its " +
                $"{patched} particle locally");
    }
}
