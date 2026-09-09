using Terraria.ModLoader;

namespace HodgepodgeServer;

// A ModPlayer.PreUpdate, so it runs once per player per tick. It resolves five mods by name,
// reads screen coordinates, and derives several dozen biome and time-of-day flags into static
// fields -- all from Main.player[Main.myPlayer], which on a dedicated server is the dummy slot.
// The flags exist to pick a music track, and the server plays nothing.
public class MusicFlagsUpdate : HeadlessNoOp
{
    protected override string TargetMod => "UnCalamityModMusic";

    protected override string TargetType => "UnCalamityModMusic.Common.MusicFlags";

    protected override string TargetMethod => "PreUpdate";

    protected override bool Enabled => ModContent.GetInstance<ServerConfig>().MusicFlags;
}
