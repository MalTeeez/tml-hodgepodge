using Terraria.ModLoader;

namespace HodgepodgeServer;

// Calamity drives its foreground effects from both a draw hook and PostUpdateEverything, and only
// the latter reaches a dedicated server. There it spawns a rain particle every tick and steps the
// whole list, for a list only the draw hook reads. The one write that leaves the method,
// Main.windSpeedCurrent, is never synced -- world data carries windSpeedTarget -- so each client
// derives its own wind regardless.
public class ForegroundRainUpdate : HeadlessNoOp
{
    protected override string TargetMod => "CalamityMod";

    protected override string TargetType =>
        "CalamityMod.ForegroundDrawing.LoopingTextures.LoopingTextureForeground";

    protected override string TargetMethod => "PostUpdateEverything";

    protected override bool Enabled => ModContent.GetInstance<ServerConfig>().ForegroundRain;
}
