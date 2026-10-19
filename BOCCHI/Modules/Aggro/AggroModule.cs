using System;
using BOCCHI.Automation;
using BOCCHI.Data;
using BOCCHI.Modules.Notifications;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using Ocelot.Modules;
using Ocelot.Windows;

namespace BOCCHI.Modules.Aggro;

public enum AggroScanAction : byte
{
    /// <summary>沒人要用：清空快照與區域，不掃。</summary>
    Clear = 0,

    /// <summary>還沒到下一次迴避掃描：保留上一次的結果。</summary>
    Wait = 1,

    Scan = 2,
}

/// <summary>新月島仇恨顯示，以及 BOCCHI 自己帶你趕路時繞開仇恨範圍（見 <see cref="AggroAvoidance"/>）。</summary>
[OcelotModule(1006, 7)]
public sealed class AggroModule(Plugin plugin, Config config) : Module(plugin, config)
{
    // 快照超過這麼久沒更新（Update 被略過：過場、切換區域）就不畫，避免把舊位置留在畫面上。
    private const long MaxSnapshotAgeMs = 250;

    // 顯示關著、只為趕路迴避掃描時的間隔：迴避不需要每幀的位置。
    public const long AvoidScanIntervalMs = 100;

    public override AggroConfig Config
    {
        get => PluginConfig.AggroConfig;
    }

    public override bool IsEnabled
    {
        get => Config.Enabled;
    }

    // 功能關閉時也追蹤知見等級（只讀欄位）：實機 log 才有核對依據，區域等級上限跟隨也用得到。
    public override bool ShouldUpdate
    {
        get => true;
    }

    public readonly KnowledgeLevelTracker Knowledge = new();

    public readonly AggroScanner Scanner = new();

    private readonly PictomancyAggroSink sink = new();

    public readonly AggroZoneSet Zones = new();

    public readonly AggroAvoidance Avoidance = new();

    public readonly WalkThrough Walker = new();

    public readonly EurekaPotTravel PotTravel = new();

    private readonly Panel panel = new();

    private long nextAvoidScanAt;

    public override void Update(UpdateContext context)
    {
        if (Knowledge.Update() && PluginConfig.NotificationsConfig.PraiseOccultEvents)
        {
            TataruPraiseIpc.Praise(TataruPraiseIpc.KnowledgeLevelUp, $"知見等級提升到 {Knowledge.Current.Real}");
        }

        var now = Environment.TickCount64;
        PotTravel.Update(now, this);
        var display = Config.Enabled;
        var avoiding = Config.AvoidWhileMoving && IsTravelling();
        switch (PlanScan(display, avoiding, now, ref nextAvoidScanAt))
        {
            case AggroScanAction.Clear:
                Scanner.Snapshot.Clear();
                Zones.Clear();
                break;
            case AggroScanAction.Scan:
                Scanner.Scan(BuildContext());
                if (avoiding)
                {
                    Zones.Build(Scanner.Snapshot.Entries, BuildZoneSettings(Svc.Condition[ConditionFlag.Mounted]), now);
                }
                else
                {
                    Zones.Clear();
                }

                break;
        }

        Avoidance.Update(now, this);
        Walker.Request(now, Avoidance.Walk);
    }

    public override void Dispose()
    {
        Walker.Dispose();
    }

    /// <summary>
    /// 顯示開：每幀掃。只剩趕路迴避要用：每 <see cref="AvoidScanIntervalMs"/> 掃一次（推進 nextAvoidScanAt）。
    /// 兩者都不要：清空、不掃。
    /// </summary>
    public static AggroScanAction PlanScan(bool display, bool avoiding, long now, ref long nextAvoidScanAt)
    {
        if (!display && !avoiding)
        {
            return AggroScanAction.Clear;
        }

        if (display)
        {
            return AggroScanAction.Scan;
        }

        if (now < nextAvoidScanAt)
        {
            return AggroScanAction.Wait;
        }

        nextAvoidScanAt = now + AvoidScanIntervalMs;
        return AggroScanAction.Scan;
    }

    /// <summary>有沒有一趟 BOCCHI 送出、還沒走到的趕路（見 <see cref="TravelIntent"/>）。</summary>
    public static bool IsTravelling()
    {
        if (!TravelIntent.State.HasIntent)
        {
            return false;
        }

        var player = Svc.Objects.LocalPlayer;
        return player != null && TravelIntent.State.TryGetActive(Svc.ClientState.TerritoryType, player.Position, Environment.TickCount64, out _);
    }

    public override void Render(RenderContext context)
    {
        var snapshot = Scanner.Snapshot;
        if (snapshot.Count == 0 || !snapshot.IsFresh(Environment.TickCount64, MaxSnapshotAgeMs))
        {
            return;
        }

        sink.Begin(context.Pictomancy);
        try
        {
            AggroRenderer.Draw(snapshot.Entries, BuildDrawSettings(), sink);
        }
        finally
        {
            sink.End();
        }
    }

    public override bool RenderMainUi(RenderContext context)
    {
        panel.Draw(this);
        return true;
    }

    public AggroContext BuildContext()
    {
        var hp = -1f;
        var player = Svc.Objects.LocalPlayer;
        if (player is { MaxHp: > 0 })
        {
            hp = (float)player.CurrentHp / player.MaxHp;
        }

        return new AggroContext(
            Knowledge.Current.Real,
            hp,
            Config.BloodHpThreshold / 100f,
            AggroJudge.UndeadNightAt(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            Scanner.Calibration.Reliable,
            Config.ShowUnknown);
    }

    public AggroDrawSettings BuildDrawSettings()
    {
        var config = Config;
        return new AggroDrawSettings(
            config.DrawSight,
            config.SightRadius,
            Math.Clamp(config.SightAngle, 1, 360) * MathF.PI / 360f,
            config.DrawSound,
            config.SoundRadius,
            config.DrawProximity,
            config.ProximityRadius,
            config.DrawMagic,
            config.MagicRadius,
            config.DrawBlood,
            config.BloodRadius,
            config.ShowUnknown,
            config.UnknownRadius);
    }

    public AggroZoneSettings BuildZoneSettings(bool mounted)
    {
        var config = Config;
        return new AggroZoneSettings(
            config.SightRadius,
            Math.Clamp(config.SightAngle, 1, 360) * MathF.PI / 360f,
            config.SoundRadius,
            config.ProximityRadius,
            config.MagicRadius,
            config.BloodRadius,
            config.UnknownRadius,
            Math.Clamp(config.AvoidMargin, 0f, 10f),
            mounted);
    }
}
