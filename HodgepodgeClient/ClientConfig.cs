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

    [DefaultValue(true)]
    [ReloadRequired]
    public bool LostColosseumSceneCheck { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool ExoMechSkyLookup { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool LootBeamBatchMode { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool WallClockDateCache { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool SubworldActiveScan { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool NearbyEffectsBlockDispatch { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool PolarityPulseBatchGuard { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool WorkshopTileCenterReuse { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool DrawTimeLogging { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool TilePreDrawContentIds { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool BossIntroScreenGate { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool MusicEventTrackLookup { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool ShaderDrawerSortDiscarded { get; set; }

    // Off until a before/after exists: the saving is on the lighting worker threads, and how much
    // of that reaches the main thread's wait on them is not something the traces can say.
    [DefaultValue(false)]
    [ReloadRequired]
    public bool TileLightProjectileBonus { get; set; }

    // Off until a before/after exists: this one takes over a NoxusBoss feature rather than
    // removing work, the same reason FullBrightDispatch ships off.
    [DefaultValue(false)]
    [ReloadRequired]
    public bool ForgeRecipeConditionDispatch { get; set; }

    // Off until a before/after exists. This is the only patch here that edits the graphics
    // pipeline itself rather than one mod, so it is the only one whose blast radius is every mod
    // that draws. The survey behind it holds for the current mod list and no other.
    [DefaultValue(false)]
    [ReloadRequired]
    public bool EmptyBatchFlush { get; set; }

    // Off until a before/after exists: this one replaces a SOTS feature rather than removing work.
    [DefaultValue(false)]
    [ReloadRequired]
    public bool FullBrightDispatch { get; set; }
}
