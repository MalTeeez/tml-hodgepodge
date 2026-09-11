using Terraria.ModLoader;

namespace HodgepodgeClient;

// The Pharaoh's Curse foam. Read against SOTS 0.25.1.9.
public class CurseFoamDrawGate : FoamDrawGate
{
    protected override string TargetMod => "SOTS";

    protected override string TargetType => "SOTS.CurseHelper";

    protected override string Label => "Curse foam draw gate";

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().CurseFoamDrawGate;
}
