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

    [DefaultValue(true)]
    [ReloadRequired]
    public bool AprilFoolsTextureCheck { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool BlueMoonBuffLookup { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool WallClockDateCache { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool XykWingCountScan { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool RepeatedTextFormat { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool DebuffProjectileScan { get; set; }
}
