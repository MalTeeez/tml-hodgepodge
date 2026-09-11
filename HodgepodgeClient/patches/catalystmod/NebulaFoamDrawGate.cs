using Terraria.ModLoader;

namespace HodgepodgeClient;

// The Interstellar Corruption foam. Read against CatalystMod 1.1.8.
public class NebulaFoamDrawGate : FoamDrawGate
{
    protected override string TargetMod => "CatalystMod";

    protected override string TargetType => "CatalystMod.CatalystParticleHelper";

    protected override string Label => "Nebula foam draw gate";

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().NebulaFoamDrawGate;
}
