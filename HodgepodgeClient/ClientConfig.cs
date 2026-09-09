using System.ComponentModel;
using Terraria.ModLoader.Config;

namespace HodgepodgeClient;

public class ClientConfig : ModConfig
{
    public override ConfigScope Mode => ConfigScope.ClientSide;

    [DefaultValue(true)]
    [ReloadRequired]
    public bool TileEdgeHighlightGuard { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool ConstellationStarBuffer { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool ForegroundDoubleUpdate { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool PlayerPostProcessingRedraw { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool AprilFoolsDateCheck { get; set; }

    // Off until a before/after exists: this one replaces a SOTS feature rather than removing work.
    [DefaultValue(false)]
    [ReloadRequired]
    public bool FullBrightDispatch { get; set; }
}
