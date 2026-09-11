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
    public bool DebuffProjectileScan { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool ShaderDrawerSortDiscarded { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool CurseDustPixelCache { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool CurseIconDrawGate { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool LootBeamTextureCache { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool WhipBuffImmunityScan { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool BlueMoonBuffLookup { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool AstrageldonPresenceScan { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool LingeringFieldParticleBroadcast { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool LingeringFieldParticleVisibility { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool InfernalItemBalanceCache { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool InfernalBossBuffScan { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool TimeFrozenIdLookup { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool ResetEffectsContentIds { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool VoidAnomalyRangeGuard { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool CurseFoamDrawGate { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool NebulaFoamDrawGate { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool PlasmaGenBossScan { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool BossPromptPresenceScan { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool NuclearTorrentInvisibleDraw { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool SeaPrismPresenceScan { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool EmptyPixelationTargetDraw { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool MusicFlagsLocalPlayer { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool CritterBestiaryRegistration { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool XykWingCountScan { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool PlayerDrawTextureCache { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool AuricSoulSceneItemIds { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool StellarNovaTextGate { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool CosmosMetaballIdleDraw { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool HeldItemSnapshotClone { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool MouseItemSlotClone { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool LumenylCrystalScan { get; set; }

    // Opt-in because this detours a tModLoader property read by every mod's content.
    [DefaultValue(false)]
    [ReloadRequired]
    public bool ModTypeNameCache { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool SceneEffectWeightCapacity { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool TileLightProjectileBonus { get; set; }

    // Opt-in because this replaces a NoxusBoss feature rather than removing dead work.
    [DefaultValue(false)]
    [ReloadRequired]
    public bool ForgeRecipeConditionDispatch { get; set; }

    [DefaultValue(true)]
    [ReloadRequired]
    public bool FullBrightDispatch { get; set; }

    // Opt-in because this changes particle reuse and draw order in a shared vanilla pool type.
    [DefaultValue(false)]
    [ReloadRequired]
    public bool ParticlePoolScanCursor { get; set; }
}
