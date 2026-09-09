using System.ComponentModel;
using Terraria.ModLoader.Config;

namespace HodgepodgeServer;

public class ServerConfig : ModConfig
{
    public override ConfigScope Mode => ConfigScope.ServerSide;

    [DefaultValue(true)]
    [ReloadRequired]
    public bool MusicFlags { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool ForegroundRain { get; set; }
}
