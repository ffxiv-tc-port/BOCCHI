using System;
using BOCCHI.Automation;
using BOCCHI.Data;
using BOCCHI.Ledger;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.StateManager;
using BOCCHI.Pathfinding;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Ocelot.Chain;
using Ocelot.IPC;

namespace BOCCHI.Modules.Automator;

// 本檔契約：只在框架執行緒用（Automator.PostUpdate 與它送出的動作鏈）；物件與原生指標當幀讀完即丟。
// 不是參加的活動：不進活動帳、沒有目標；何時開始與換掉由 Automator 決定，進塔後由 AutomationControl 全部停止。
// 刻意不做：報名、使用魔紋啟動憑證、和任何物件互動——一律由使用者自己操作，BOCCHI 只走到平台上等。

/// <summary>
/// 設定「兩歧塔開放時到傳送魔紋等候」：走到平台中心站著等被傳送進塔。
/// 設定關掉、報名截止、開打後 <see cref="ForkedTowerPad.TeleportGraceMs"/> 內沒被傳送進去就失效。
/// </summary>
public sealed class ForkedTowerWaitActivity : Activity
{
    private const string Tag = "[BOCCHI][兩歧塔]";

    private readonly CriticalEncountersModule source;

    private readonly System.Numerics.Vector3 pad;

    private readonly string name;

    private readonly long departedAt;

    // 只有魔紋中央一個點：判斷 vnavmesh 正在走的是不是走向魔紋的路。
    private readonly OwnMoveDestinations padDestination = new();

    // 當幀清單裡沒有這一場（讀取中清單被清空）時用最後一次看到的值。
    private CriticalEncounterSnapshot lastSeen;

    // 第一次看到開打（Battle）的時刻；0＝還沒看到。
    private long battleSeenAt;

    private bool arrivedReported;

    private bool endReported;

    public ForkedTowerWaitActivity(EventData data, Lifestream lifestream, VNavmesh vnav, AutomatorModule module, CriticalEncountersModule source, System.Numerics.Vector3 pad)
        : base(data, lifestream, vnav, module)
    {
        this.source = source;
        this.pad = pad;
        source.CriticalEncounters.TryGetValue(data.Id, out lastSeen);
        name = module.T("panel.forked_tower_wait").Replace("{name}", string.IsNullOrEmpty(lastSeen.Name) ? data.InternalName : lastSeen.Name);
        departedAt = Environment.TickCount64;
        padDestination.Add(pad);
        // 在平台上報名後遊戲可能已把角色算進這一場（基底會當成參加中）：這裡一律從趕路開始。
        state = ActivityState.Idle;
        handlers[ActivityState.WaitingForForkedTower] = GetWaitChain;
    }

    /// <summary>報名截止（Unix 秒）：「換一場」分辨場次用。</summary>
    public int StartTimestamp
    {
        get => lastSeen.StartTimestamp;
    }

    public override bool IsValid()
    {
        return WhyInvalid() == null;
    }

    // null＝還要等；否則是給記錄用的原因。
    private string? WhyInvalid()
    {
        if (!module.Config.ShouldWaitAtForkedTowerPad)
        {
            return "設定關了";
        }

        if (Svc.Objects.LocalPlayer is { } player && ForkedTowerPad.IsInsideTower(player))
        {
            return "已經進塔";
        }

        if (source.CriticalEncounters.TryGetValue(data.Id, out var current))
        {
            lastSeen = current;
        }
        else
        {
            return "兩歧塔已不在清單上";
        }

        if (ForkedTowerPad.IsRegistrationOpen(current.State))
        {
            battleSeenAt = 0;
            return null;
        }

        if (current.State != DynamicEventState.Battle || state != ActivityState.WaitingForForkedTower)
        {
            return $"報名已截止（狀態 {current.State}）";
        }

        var now = Environment.TickCount64;
        if (battleSeenAt == 0)
        {
            battleSeenAt = now;
        }

        return now - battleSeenAt <= ForkedTowerPad.TeleportGraceMs ? null : $"開打 {ForkedTowerPad.TeleportGraceMs / 1000} 秒後還沒被傳送進塔";
    }

    protected override System.Numerics.Vector3 GetPosition()
    {
        return pad;
    }

    protected override float GetRadius()
    {
        return ForkedTowerPad.ArriveM;
    }

    public override string GetName()
    {
        return name;
    }

    public override int ReadProgress()
    {
        return 0;
    }

    public override string KindLabel
    {
        get => "TOWER";
    }

    protected override bool IsActivityTarget(IBattleNpc obj)
    {
        return false;
    }

    protected override ActivityState GetPostPathfindingState()
    {
        return ActivityState.WaitingForForkedTower;
    }

    public override void LogTargetSummary(ActivityEnd end)
    {
    }

    // 同 CriticalEncounter：只用插件自己看到的報名開始，看不到就讓呼叫端從選定算（只會多等）。
    protected override long RegistrationOpenAgeMs(out string unknownReason)
    {
        if (!source.History.TryGetOpen((ushort)data.Id, out var entry))
        {
            unknownReason = "沒有這一場的紀錄";
            return -1;
        }

        if (entry.FirstSeenState != DynamicEventState.Register)
        {
            unknownReason = "第一次看到時已過了報名";
            return -1;
        }

        unknownReason = string.Empty;
        return Math.Max(0L, (long)(DateTime.UtcNow - entry.FirstSeenUtc).TotalMilliseconds);
    }

    protected override TaskManagerTask GetPathfindingWatcher(StateManagerModule states)
    {
        var stops = new TravelStopGuard();
        return new TaskManagerTask(() =>
        {
            TakeReturnHandOver(stops);
            if (!IsValid())
            {
                throw new Exception("Activity is no longer valid.");
            }

            var stopped = !IsNavigating();
            ObserveTravel(stops, !stopped);
            if (Svc.Objects.LocalPlayer is { } player && ForkedTowerPad.IsOnPad(player.Position, pad))
            {
                if (!stopped)
                {
                    OwnPathStop.StopIfOwn(vnav, padDestination, out _);
                }

                return true;
            }

            if (PauseForCombat(states, stopped))
            {
                return false;
            }

            if (!stopped)
            {
                KeepTravelling(stops, true, TravelSource.IllegalModeCriticalEncounter);
                return false;
            }

            if (KeepTravelling(stops, false, TravelSource.IllegalModeCriticalEncounter))
            {
                return false;
            }

            ReportTravelInterrupt("vnav停下");
            throw new VnavmeshStoppedException();
        }, TravelWatcherConfig(stops));
    }

    // 回 true＝離平台太遠，回到趕路（逾時後也是重建這條鏈，不算離開）。
    private Func<Chain> GetWaitChain(StateManagerModule states)
    {
        return () => Chain.Create("Illegal:WaitForForkedTower")
            .Then(new TaskManagerTask(TickWait, new TaskManagerConfiguration { TimeLimitMS = 30 * 60 * 1000, ShowError = false }))
            .Then(_ => state = ActivityState.Pathfinding);
    }

    private bool TickWait()
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        var position = player.Position;
        if (!arrivedReported)
        {
            arrivedReported = true;
            Svc.Log.Information($"{Tag} 抵達傳送魔紋：離平台中心 {ForkedTowerPad.DistanceXZ(position, pad):F1} 米（趕路 {Elapsed()}），在這裡等被傳送進塔；不會替你使用魔紋啟動憑證");
            Svc.Chat.Print(module.T("messages.forked_tower_on_pad"));
        }

        if (IsNavigating() || Svc.Condition[ConditionFlag.InCombat])
        {
            return false;
        }

        var off = ForkedTowerPad.DistanceXZ(position, pad);
        if (off <= ForkedTowerPad.LeashM)
        {
            return false;
        }

        Svc.Log.Information($"{Tag} 等候中離平台中心 {off:F1} 米（超過 {ForkedTowerPad.LeashM:0} 米）：走回傳送魔紋");
        return true;
    }

    /// <summary>開始前往時呼叫一次。<paramref name="left"/> 是放下的活動（記錄用，沒有給 null）。</summary>
    public void ReportDeparted(Activity? left, string navigation)
    {
        var distance = Svc.Objects.LocalPlayer is { } player ? $"{ForkedTowerPad.DistanceXZ(player.Position, pad):F1} 米" : "?";
        var from = left == null ? "沒有進行中的活動" : $"放下{left.KindLabel}「{left.GetName()}」（{left.state}）";
        Svc.Log.Information($"{Tag} 開放報名（狀態 {lastSeen.State}，報名截止 {lastSeen.StartTimestamp}）：{from}，前往傳送魔紋 ({pad.X:F1}, {pad.Y:F1}, {pad.Z:F1})，離平台 {distance}；導航：{navigation}");
    }

    /// <summary>失效時（Automator 換掉它之前）呼叫；只寫第一次。</summary>
    public void ReportEnded()
    {
        if (endReported)
        {
            return;
        }

        endReported = true;
        Svc.Log.Information($"{Tag} 結束：{WhyInvalid() ?? "?"}（從出發起 {Elapsed()}，{(arrivedReported ? "到過傳送魔紋" : "沒走到傳送魔紋")}）：照常選活動");
    }

    /// <summary>非法模式停止或換區（Automator.Refresh）時呼叫。</summary>
    public void ReportStopped(ActivityEnd end)
    {
        if (endReported)
        {
            return;
        }

        endReported = true;
        Svc.Log.Information($"{Tag} 結束：{LedgerRules.Label(end)}（從出發起 {Elapsed()}，{(arrivedReported ? "到過傳送魔紋" : "沒走到傳送魔紋")}）");
    }

    private string Elapsed()
    {
        return LedgerRules.Seconds(Environment.TickCount64 - departedAt) + " 秒";
    }
}
