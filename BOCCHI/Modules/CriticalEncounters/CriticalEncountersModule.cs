using System;
using System.Collections.Generic;
using BOCCHI.Data;
using BOCCHI.Modules.Notifications;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Ocelot.Modules;
using Ocelot.Windows;

namespace BOCCHI.Modules.CriticalEncounters;

[OcelotModule(1002, 6)]
public class CriticalEncountersModule : Module
{
    public override CriticalEncountersConfig Config
    {
        get => PluginConfig.CriticalEncountersConfig;
    }

    public override bool IsEnabled
    {
        get => Config.IsPropertyEnabled(nameof(Config.Enabled));
    }

    public override bool ShouldUpdate
    {
        get => true;
    }

    public readonly CriticalEncounterTracker Tracker;

    public Dictionary<uint, CriticalEncounterSnapshot> CriticalEncounters
    {
        get => Tracker.CriticalEncounters;
    }

    public Dictionary<uint, EventProgress> Progress
    {
        get => Tracker.Progress;
    }

    /// <summary>本次進島的紀錄；不論有沒有開「顯示紀錄」都在記（只是記憶體裡的清單）。</summary>
    public readonly CriticalEncounterHistory History = new();

    private readonly Panel panel = new();

    private readonly Alerter alerter;

    /// <summary>跟團刷 CE（主視窗武裝；只在記憶體）。</summary>
    public readonly CeFarm Farm;

    public CriticalEncountersModule(Plugin plugin, Config config)
        : base(plugin, config)
    {
        Tracker = new CriticalEncounterTracker(this);
        alerter = new Alerter(this);
        Farm = new CeFarm(this);
        History.OnClosed = LogClosed;

        // 刻意直接訂閱、不靠 OnTerritoryChanged：ModuleManager 轉發換區時用的是上一次在島上更新過的模組清單。
        Svc.ClientState.TerritoryChanged += OnZoneChanged;
    }

    public override void Update(UpdateContext context)
    {
        Tracker.Tick(context.Framework);
        History.Observe(Tracker.CriticalEncounters, DateTime.UtcNow, Tracker.CurrentEventId);
    }

    private void OnZoneChanged(ushort id)
    {
        Tracker.Reset();
        if (id == ZoneData.SOUTHHORN)
        {
            History.Reset();
        }
    }

    // 不論「顯示紀錄」開不開都寫：那個設定只管面板上畫不畫。
    private void LogClosed(CriticalEncounterHistory.Entry entry)
    {
        var seen = entry.FirstSeenState == DynamicEventState.Register ? "開放報名時" : "已過了報名";
        var battle = entry.BattleStartUtc is { } start && entry.EndUtc is { } end ? $"戰鬥 {(int)(end - start).TotalMinutes}:{(end - start).Seconds:D2}" : "沒有進入戰鬥";
        var outcome = entry.Outcome switch
        {
            CriticalEncounterOutcome.Success => "成功",
            CriticalEncounterOutcome.Failure => "失敗",
            _ => "不明",
        };
        Svc.Log.Information(
            $"[BOCCHI][緊急遭遇戰紀錄] 「{entry.Name}」({entry.Id}) 結束：第一次看到 {entry.FirstSeenUtc.ToLocalTime():HH:mm:ss}（{seen}）、{battle}、最後進度 {entry.LastProgress}%、剩 {entry.LastSecondsLeft} 秒、最多 {entry.MaxParticipants} 人{(entry.Unobserved ? "、結束前一刻沒在更新" : string.Empty)}；參加={(entry.Participated ? "是" : "否")} 結果={outcome}");

        if (PluginConfig.NotificationsConfig.PraiseOccultEvents && CriticalEncounterHistory.ShouldPraise(entry))
        {
            TataruPraiseIpc.Praise(TataruPraiseIpc.CriticalEncounterSuccess, $"緊急遭遇戰「{entry.Name}」成功");
        }
    }

    public override bool RenderMainUi(RenderContext context)
    {
        panel.Draw(this);
        return true;
    }

    public override void OnTerritoryChanged(ushort id)
    {
        CriticalEncounters.Clear();
    }

    public override void Dispose()
    {
        base.Dispose();
        alerter.Dispose();
        Farm.Dispose();
        Svc.ClientState.TerritoryChanged -= OnZoneChanged;
    }
}
