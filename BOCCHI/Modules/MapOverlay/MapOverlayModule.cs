using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using BOCCHI.Automation;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.Automator;
using BOCCHI.Modules.Carrots;
using BOCCHI.Modules.Treasure;
using BOCCHI.Pathfinding;
using ECommons.DalamudServices;
using Ocelot;
using Ocelot.Modules;

namespace BOCCHI.Modules.MapOverlay;

// 本檔契約：全部在框架執行緒（模組 Update、TerritoryChanged／Logout、Dispose）。巡迴步驟、寶箱記錄、趕路登記都在同一條執行緒寫，
// 當幀讀完、推送時另外複製成新陣列；不保存遊戲物件或原生指標（寶箱版面只在讀的那一次呼叫裡用）。

/// <summary>
/// 把寶箱巡迴的規劃順序、寶箱生成點三態、幸運胡蘿蔔巡迴中的生成點、目前的趕路目的地、等魔法甕的出生點與等待點推給 Mappy 的疊加層。
/// Mappy 沒裝或沒有疊加層 API 時只每 5 秒問一次、不做別的。實際要走的路線由 Mappy 自己向 vnavmesh 讀，這裡不推。
/// </summary>
[OcelotModule(1009)]
public class MapOverlayModule(Plugin plugin, Config config) : Module(plugin, config), IMapOverlaySource
{
    public override MapOverlayConfig Config
    {
        get => PluginConfig.MapOverlayConfig;
    }

    public override bool ShouldInitialize
    {
        get => true;
    }

    private const long LayoutRetryMs = 5000;

    private TreasureModule? treasure;

    private CarrotsModule? carrots;

    private AutomatorModule? automator;

    private MapOverlayPusher? pusher;

    private bool subscribed;

    // 還沒開始寶箱巡迴（記錄裡沒有生成點）時自己讀一次版面，只拿來畫；不交給寶箱記錄（那裡進島後到第一次規劃前刻意不記巡過）。
    private readonly List<TreasureData.TreasureDatum> layoutBuffer = [];

    private TreasureSpawnPoint[] layoutPoints = [];

    private long nextLayoutReadAt;

    private string[]? routeNames;

    private readonly List<Vector3> seenCarrots = [];

    // TryGetSignature 當幀抄下、緊接著的 Build 用。
    private TravelIntentSnapshot intent;

    private PotWaitActivity? wait;

    public override void PostInitialize()
    {
        TryGetModule(out treasure);
        TryGetModule(out carrots);
        TryGetModule(out automator);
        pusher = new MapOverlayPusher(new MappyOverlayClient(Svc.PluginInterface, ZoneData.SOUTHHORN));
        Svc.ClientState.TerritoryChanged += OnZoneChanged;
        Svc.ClientState.Logout += OnLogout;
        subscribed = true;
    }

    // 這一層出錯不可以擋到別的模組：模組 Update 擲出會讓同一幀所有模組的 PostUpdate（非法模式）都被跳過。
    public override void Update(UpdateContext context)
    {
        try
        {
            pusher?.Tick(Environment.TickCount64, Config.ShowOnMappy && ZoneData.IsInOccultCrescent(), this);
        }
        catch (Exception ex)
        {
            ReportFailure(ex, "推送");
        }
    }

    public override void Dispose()
    {
        if (subscribed)
        {
            subscribed = false;
            Svc.ClientState.TerritoryChanged -= OnZoneChanged;
            Svc.ClientState.Logout -= OnLogout;
        }

        ResetZone();
        base.Dispose();
    }

    private void OnZoneChanged(ushort territory)
    {
        ResetZone();
    }

    private void OnLogout(int type, int code)
    {
        ResetZone();
    }

    private void ResetZone()
    {
        layoutPoints = [];
        nextLayoutReadAt = 0;
        try
        {
            pusher?.Clear(Environment.TickCount64);
        }
        catch (Exception ex)
        {
            ReportFailure(ex, "清除");
        }
    }

    private bool failureReported;

    private void ReportFailure(Exception ex, string what)
    {
        if (failureReported)
        {
            return;
        }

        failureReported = true;
        Svc.Log.Warning($"[BOCCHI][Mappy] {what}疊加層時出錯（{ex.GetType().Name}：{ex.Message}）；之後的錯誤不再記");
    }

    public bool TryGetSignature(MapOverlayGroup group, long now, out int signature)
    {
        signature = 0;
        switch (group)
        {
            case MapOverlayGroup.Plan:
            {
                var hunter = treasure?.Hunter;
                if (hunter is not { IsRunning: true } || hunter.CurrentStepIndex >= hunter.PlannedSteps.Count)
                {
                    return false;
                }

                SpawnPositions(now);
                signature = HashCode.Combine(RuntimeHelpers.GetHashCode(hunter.PlannedSteps), hunter.PlannedSteps.Count, hunter.CurrentStepIndex, hunter.RoundReversed,
                    hunter.Direction, PositionStamp(), LanguageStamp());
                return true;
            }
            case MapOverlayGroup.Spawn:
            {
                if (treasure == null)
                {
                    return false;
                }

                var memory = treasure.HuntMemory;
                if (SpawnPositions(now).Count == 0 && memory.ChestCount == 0)
                {
                    return false;
                }

                signature = HashCode.Combine(memory.Version, PositionStamp(), Config.ShowVisitedSpawnPoints, LanguageStamp());
                return true;
            }
            case MapOverlayGroup.Carrot:
            {
                var hunter = carrots?.Hunter;
                if (hunter is not { IsRunning: true })
                {
                    return false;
                }

                var seen = 17;
                seenCarrots.Clear();
                foreach (var carrot in carrots!.carrots)
                {
                    var at = carrot.LastKnownPosition;
                    seenCarrots.Add(at);
                    seen = HashCode.Combine(seen, (int)MathF.Round(at.X), (int)MathF.Round(at.Z));
                }

                signature = HashCode.Combine(hunter.Progress.VisitedCount, hunter.Progress.PlannedCount, seen, LanguageStamp());
                return true;
            }
            case MapOverlayGroup.Destination:
            {
                var player = Svc.Objects.LocalPlayer;
                if (player == null || !TravelIntent.State.TryGetActive(Svc.ClientState.TerritoryType, player.Position, now, out intent))
                {
                    return false;
                }

                signature = HashCode.Combine(intent.Sequence, intent.Generation, intent.Source, LanguageStamp());
                return true;
            }
            case MapOverlayGroup.Pot:
            {
                wait = automator?.automator.Activity as PotWaitActivity;
                if (wait == null)
                {
                    return false;
                }

                var hold = wait.HoldPoint is { } h ? HashCode.Combine((int)MathF.Round(h.X), (int)MathF.Round(h.Z)) : 0;
                signature = HashCode.Combine(RuntimeHelpers.GetHashCode(wait), hold, LanguageStamp());
                return true;
            }
            default:
                return false;
        }
    }

    public void Build(MapOverlayGroup group, List<OverlayLayer> into)
    {
        switch (group)
        {
            case MapOverlayGroup.Plan when treasure?.Hunter is { } hunter:
                MapOverlayLayers.BuildPlan(hunter.PlannedSteps, hunter.CurrentStepIndex, TryGetNodePosition, Texts(hunter), into);
                break;
            case MapOverlayGroup.Spawn when treasure != null:
                MapOverlayLayers.BuildSpawnPoints(SpawnPositions(Environment.TickCount64), treasure.HuntMemory, Config.ShowVisitedSpawnPoints, Texts(null), into);
                break;
            case MapOverlayGroup.Carrot when carrots?.Hunter is { } hunter:
                MapOverlayLayers.BuildCarrots(CarrotData.Data, hunter.Progress, seenCarrots, Texts(null), into);
                break;
            case MapOverlayGroup.Destination:
                MapOverlayLayers.BuildDestination(intent.Destination, T("label.destination").Replace("{source}", T(SourceKey(intent.Source))), into);
                break;
            case MapOverlayGroup.Pot when wait != null:
                MapOverlayLayers.BuildPotWait(wait.Spawn, wait.HoldPoint, T("label.pot_spawn").Replace("{pot}", wait.PotName), T("label.pot_wait").Replace("{pot}", wait.PotName), into);
                wait = null;
                break;
        }
    }

    // 記錄裡有生成點就用記錄的（巡迴開始後一定有）；沒有就自己讀版面，失敗每 LayoutRetryMs 重試、讀到就不再讀（換區或登出才重來）。
    private IReadOnlyList<TreasureSpawnPoint> SpawnPositions(long now)
    {
        var memory = treasure!.HuntMemory;
        if (memory.HasSpawnPoints)
        {
            return memory.SpawnPoints;
        }

        if (layoutPoints.Length > 0 || now < nextLayoutReadAt)
        {
            return layoutPoints;
        }

        nextLayoutReadAt = now + LayoutRetryMs;
        layoutBuffer.Clear();
        if (!TreasureLayout.TryRead(layoutBuffer, out _))
        {
            return layoutPoints;
        }

        var points = new List<TreasureSpawnPoint>(layoutBuffer.Count);
        foreach (var datum in layoutBuffer)
        {
            if (TreasureData.Levels.ContainsKey(datum.Id))
            {
                points.Add(new TreasureSpawnPoint(datum.Id, datum.Position, datum.Type == TreasureLayout.SilverModel ? TreasureType.Silver : TreasureType.Bronze));
            }
        }

        points.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        layoutPoints = points.ToArray();
        return layoutPoints;
    }

    private bool TryGetNodePosition(uint node, out Vector3 position)
    {
        if (treasure != null && treasure.HuntMemory.TryGetSpawnPoint(node, out var point))
        {
            position = point.Position;
            return true;
        }

        foreach (var candidate in layoutPoints)
        {
            if (candidate.Id == node)
            {
                position = candidate.Position;
                return true;
            }
        }

        position = default;
        return false;
    }

    private int PositionStamp()
    {
        return HashCode.Combine(treasure?.HuntMemory.SpawnPointCount ?? 0, layoutPoints.Length);
    }

    // 換語言時翻譯字串換一個實例：拿實例身分當簽章的一部分，換語言就重推。
    private int LanguageStamp()
    {
        return RuntimeHelpers.GetHashCode(T("label.plan_node"));
    }

    private MapOverlayTexts Texts(Hunter? hunter)
    {
        if (routeNames == null)
        {
            routeNames = new string[TreasureRoutes.Order.Length];
            for (var i = 0; i < routeNames.Length; i++)
            {
                var name = TreasureRoutes.Order[i].ToFriendlyString();
                routeNames[i] = string.IsNullOrEmpty(name) ? TreasureRoutes.Order[i].ToString() : name;
            }
        }

        var direction = string.Empty;
        if (hunter != null)
        {
            direction = I18N.T(hunter.RoundReversed ? "hunter.direction.reverse" : "hunter.direction.forward");
            if (hunter.Direction == HuntDirection.Alternate)
            {
                direction = T("label.direction_alternate").Replace("{direction}", direction).Replace("{alternate}", I18N.T("hunter.direction.alternate"));
            }
        }

        return new MapOverlayTexts
        {
            RouteNames = routeNames,
            Direction = direction,
            PlanNodeTemplate = T("label.plan_node"),
            PlanShardTemplate = T("label.plan_shard"),
            PlanTeleportTemplate = T("label.plan_teleport"),
            PlanReturn = T("label.plan_return"),
            SpawnOpenTemplate = T("label.spawn_open"),
            SpawnVisitedTemplate = T("label.spawn_visited"),
            ChestTemplate = T("label.chest"),
            ChestUnmatchedTemplate = T("label.chest_unmatched"),
            Bronze = I18N.T("modules.treasure.panel.coffer.bronze"),
            Silver = I18N.T("modules.treasure.panel.coffer.silver"),
            AvoidedSuffix = T("label.avoided_suffix"),
            UnreachableSuffix = T("label.unreachable_suffix"),
            CarrotOpenTemplate = T("label.carrot_open"),
            CarrotDoneTemplate = T("label.carrot_done"),
            CarrotSeenTemplate = T("label.carrot_seen"),
        };
    }

    private static string SourceKey(TravelSource source)
    {
        return source switch
        {
            TravelSource.IllegalModeFate => "source.illegal_mode_fate",
            TravelSource.IllegalModeCriticalEncounter => "source.illegal_mode_critical_encounter",
            TravelSource.IllegalModeShard => "source.illegal_mode_shard",
            TravelSource.TeleporterWalk => "source.teleporter_walk",
            TravelSource.TeleporterAfterTeleport => "source.teleporter_after_teleport",
            TravelSource.NearestShard => "source.nearest_shard",
            TravelSource.Aetheryte => "source.aetheryte",
            TravelSource.KnowledgeCrystal => "source.knowledge_crystal",
            TravelSource.HuntNode => "source.hunt_node",
            TravelSource.HuntShard => "source.hunt_shard",
            TravelSource.CriticalEncounterSpot => "source.critical_encounter_spot",
            TravelSource.SilverChest => "source.silver_chest",
            TravelSource.PotWaitSpot => "source.pot_wait_spot",
            TravelSource.EurekaPotHunt => "source.eureka_pot_hunt",
            _ => "source.none",
        };
    }
}
