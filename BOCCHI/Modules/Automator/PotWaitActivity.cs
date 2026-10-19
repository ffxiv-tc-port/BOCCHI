using System;
using System.Globalization;
using System.Numerics;
using BOCCHI.ActionHelpers;
using BOCCHI.Automation;
using BOCCHI.Data;
using BOCCHI.Ledger;
using BOCCHI.Modules.Aggro;
using BOCCHI.Modules.Fates;
using BOCCHI.Modules.StateManager;
using BOCCHI.Pathfinding;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using Ocelot.Chain;
using Ocelot.IPC;

namespace BOCCHI.Modules.Automator;

// 本檔契約：只在框架執行緒用（Automator.PostUpdate 與它送出的動作鏈）；物件與原生指標一律當幀讀完即丟。
// 不是參加的活動：不進活動帳（帳上算待命）、沒有目標、不寫目標摘要；何時開始與換掉由 Automator 決定。

/// <summary>
/// 設定「魔法甕快出現時先到出生點等」：推估的下一個魔法甕快出現、又沒有別的活動可接時，先走到出生點附近，
/// 挑一個不在魔物仇恨範圍內的點等它出現。條件不再成立（出現了、推估落空、設定關掉）就失效，由 Automator 接手。
/// </summary>
public sealed class PotWaitActivity : Activity
{
    private const string Tag = "[BOCCHI][魔法甕等待]";

    private const long ZoneCheckIntervalMs = 2000;

    private const long RepositionCooldownMs = 6000;

    private const long AggroSnapshotMaxAgeMs = 400;

    // 送出移動後這麼久還沒開始走，就當這次移動結束（多半是走不到）。
    private const long MoveStartGraceMs = 1500;

    // 選到的點離目前位置不到這麼遠就不移動。
    private const float StayM = 2f;

    // 站著等時角色離等待點超過這麼遠（期間沒有戰鬥、不是 BOCCHI 在移動）＝使用者自己移動了角色。
    private const float ManualDriftM = 5f;

    private const float SnapHalfExtentXZ = 3f;

    private const float SnapHalfExtentY = 10f;

    private readonly FatesModule fates;

    private readonly Vector3 spawn;

    private readonly DateTime expectedUtc;

    private readonly string potName;

    private readonly string name;

    private readonly long departedAt;

    private AggroModule? aggro;

    private bool aggroResolved;

    // 只在框架執行緒用；挑點時當暫存區，不跨呼叫保存內容。
    private static AggroZone[]? zoneBuffer;

    // 候選點貼到導航網格的結果：地形不變，一個等待只查一次（一個都不能用時下次再查）。
    private Vector3?[]? candidates;

    private Vector3? holdPoint;

    private bool moving;

    private bool sawMoving;

    private long moveSentAt;

    private Vector3 moveTarget;

    private bool combatSinceHold;

    private bool handsOff;

    private long nextZoneCheckAt;

    private long lastRepositionAt;

    private int repositions;

    private bool arrivedReported;

    private bool combatReported;

    private bool shortMoveReported;

    private bool unsafeStayReported;

    private bool endReported;

    public PotWaitActivity(EventData data, Lifestream lifestream, VNavmesh vnav, AutomatorModule module, FatesModule fates, DateTime expectedUtc)
        : base(data, lifestream, vnav, module)
    {
        this.fates = fates;
        this.expectedUtc = expectedUtc;
        spawn = data.StartPosition ?? Vector3.Zero;
        potName = fates.potCycle.GetName(data.Id);
        name = module.T("panel.pot_wait").Replace("{pot}", potName);
        departedAt = Environment.TickCount64;
        state = ActivityState.Idle;
        handlers[ActivityState.WaitingForPot] = GetWaitChain;
    }

    public uint PotId
    {
        get => data.Id;
    }

    public Vector3 Spawn
    {
        get => spawn;
    }

    /// <summary>選好的等待點；還沒選好是 null。</summary>
    public Vector3? HoldPoint
    {
        get => holdPoint;
    }

    public string PotName
    {
        get => potName;
    }

    public override bool IsValid()
    {
        return PotWaitRules.WantsWait(module.Config, fates.potCycle.Cycle, fates.Config.GetPotCyclePeriod(), DateTime.UtcNow, out var id, out _) && id == data.Id;
    }

    protected override Vector3 GetPosition()
    {
        return spawn;
    }

    protected override float GetRadius()
    {
        return PotWaitRules.ArriveM;
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
        get => "POTWAIT";
    }

    protected override bool IsActivityTarget(IBattleNpc obj)
    {
        return false;
    }

    protected override ActivityState GetPostPathfindingState()
    {
        return ActivityState.WaitingForPot;
    }

    public override void LogTargetSummary(ActivityEnd end)
    {
    }

    protected override TaskManagerTask GetPathfindingWatcher(StateManagerModule states)
    {
        var stops = new TravelStopGuard();
        return new TaskManagerTask(() =>
        {
            TakeReturnHandOver(stops);
            var stopped = !IsNavigating();
            ObserveTravel(stops, !stopped);
            if (Svc.Objects.LocalPlayer is { } player && PotWaitRules.DistanceXZ(player.Position, spawn) <= PotWaitRules.ArriveM)
            {
                if (!stopped)
                {
                    vnav.Stop();
                }

                return true;
            }

            if (PauseForCombat(states, stopped))
            {
                return false;
            }

            if (!stopped)
            {
                KeepTravelling(stops, true, TravelSource.IllegalModeFate);
                return false;
            }

            if (KeepTravelling(stops, false, TravelSource.IllegalModeFate))
            {
                return false;
            }

            ReportTravelInterrupt("vnav停下");
            throw new VnavmeshStoppedException();
        }, TravelWatcherConfig(stops));
    }

    // 回 true＝離出生點太遠，回到趕路。每次（重新）建鏈都從目前位置重新挑點（尋寶讓位、逾時後重送都走這裡）。
    private Func<Chain> GetWaitChain(StateManagerModule states)
    {
        return () =>
        {
            holdPoint = null;
            moving = false;
            nextZoneCheckAt = 0;
            return Chain.Create("Illegal:WaitForPot")
                .Then(new TaskManagerTask(() => TickWait(states), new TaskManagerConfiguration { TimeLimitMS = 30 * 60 * 1000, ShowError = false }))
                .Then(_ => state = ActivityState.Pathfinding);
        };
    }

    private bool TickWait(StateManagerModule states)
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        var now = Environment.TickCount64;
        var position = player.Position;
        var navigating = IsNavigating();
        if (states.GetState() == State.InCombat)
        {
            HandleCombat(navigating);
            return false;
        }

        if (moving)
        {
            FollowMove(now, position, navigating);
            return false;
        }

        if (handsOff)
        {
            return false;
        }

        if (holdPoint is { } hold && !combatSinceHold && PotWaitRules.DistanceXZ(position, hold) > ManualDriftM)
        {
            handsOff = true;
            Svc.Log.Information($"{Tag} 角色離開等待點 {PotWaitRules.DistanceXZ(position, hold):F1} 米（不是 BOCCHI 移動的）：這次不再調整等待位置，魔法甕出現時照常前往");
            return false;
        }

        var fromSpawn = PotWaitRules.DistanceXZ(position, spawn);
        if (fromSpawn > PotWaitRules.ReturnM)
        {
            Svc.Log.Information($"{Tag} 離出生點 {fromSpawn:F1} 米（超過 {PotWaitRules.ReturnM:0} 米）：重新前往出生點");
            return true;
        }

        var first = holdPoint == null || combatSinceHold;
        if (!first && now < nextZoneCheckAt)
        {
            return false;
        }

        nextZoneCheckAt = now + ZoneCheckIntervalMs;
        Evaluate(now, position, fromSpawn, first);
        return false;
    }

    private void HandleCombat(bool navigating)
    {
        moving = false;
        combatSinceHold = true;
        if (navigating)
        {
            return;
        }

        Actions.TryUnmount();
        var ai = module.Config.ShouldToggleAiProvider;
        if (ai)
        {
            module.SetAi(true, "等魔法甕時遭到攻擊");
        }

        if (!combatReported)
        {
            combatReported = true;
            Svc.Log.Information($"{Tag} 等魔法甕時遭到攻擊：下坐騎{(ai ? "、打開 AI 提供者" : "（AI 提供者維持你自己的設定）")}；戰鬥結束後回等待點");
        }
    }

    private void FollowMove(long now, Vector3 position, bool navigating)
    {
        if (navigating)
        {
            sawMoving = true;
            return;
        }

        if (!sawMoving && now - moveSentAt < MoveStartGraceMs)
        {
            return;
        }

        moving = false;
        holdPoint = position;
        var off = PotWaitRules.DistanceXZ(position, moveTarget);
        if (off > PotWaitRules.MaxSnapDriftM && !shortMoveReported)
        {
            shortMoveReported = true;
            Svc.Log.Information($"{Tag} 沒走到等待點（差 {off:F1} 米{(sawMoving ? string.Empty : "，導航沒有開始走")}）：就在這裡等");
        }
    }

    private void Evaluate(long now, Vector3 position, float fromSpawn, bool first)
    {
        var known = TryReadZones(out var zones, out var minState);
        var current = PotWaitRules.Clearance(position, zones, minState);
        if (!first && !PotWaitRules.NeedsReposition(current))
        {
            return;
        }

        if (!first && now - lastRepositionAt < RepositionCooldownMs)
        {
            return;
        }

        var reason = combatSinceHold ? "戰鬥結束" : $"目前位置離仇恨範圍只剩 {Clearance(current)}";
        var afterCombat = combatSinceHold;
        combatSinceHold = false;
        if (!TryPickPoint(zones, minState, out var point, out var safe))
        {
            holdPoint = position;
            ReportArrived(fromSpawn, "導航網格查不到出生點附近可以站的點：就在這裡等", known);
            return;
        }

        var clearance = PotWaitRules.Clearance(point, zones, minState);
        if (!safe && !first)
        {
            holdPoint = position;
            lastRepositionAt = now;
            if (!unsafeStayReported)
            {
                unsafeStayReported = true;
                Svc.Log.Information($"{Tag} {reason}，但出生點附近 {PotWaitRules.Rings * PotWaitRules.RingStepM:0} 米內沒有離仇恨範圍 {PotWaitRules.ClearanceM:0} 米以上的點：留在原地");
            }

            return;
        }

        var where = $"{Format(point)}（離出生點 {PotWaitRules.DistanceXZ(point, spawn):F1} 米、離最近的仇恨範圍 {Clearance(clearance)}）";
        var choice = safe
            ? $"等待點 {where}"
            : $"出生點附近 {PotWaitRules.Rings * PotWaitRules.RingStepM:0} 米內沒有離仇恨範圍 {PotWaitRules.ClearanceM:0} 米以上的點：照最靠近出生點的點等 {where}";
        var stay = PotWaitRules.DistanceXZ(position, point) <= StayM;
        if (!stay)
        {
            if (afterCombat && module.Config.ShouldToggleAiProvider)
            {
                module.SetAi(false, "等魔法甕：戰鬥結束，回等待點");
            }

            MoveTo(now, point);
        }
        else
        {
            holdPoint = position;
        }

        if (!arrivedReported)
        {
            ReportArrived(fromSpawn, $"{(stay ? "就地等" : "走到")}{choice}", known);
            return;
        }

        if (stay)
        {
            return;
        }

        repositions++;
        lastRepositionAt = now;
        var line = $"{Tag} 換等待點（第 {repositions} 次）：{reason}，改到{choice}";
        if (repositions == 1)
        {
            Svc.Log.Information(line);
        }
        else
        {
            Svc.Log.Debug(line);
        }
    }

    private void MoveTo(long now, Vector3 point)
    {
        TravelIntent.Register(point, TravelSource.PotWaitSpot);
        OwnMoveDestinations.Note(point);
        vnav.PathfindAndMoveTo(point, false);
        moving = true;
        sawMoving = false;
        moveSentAt = now;
        moveTarget = point;
        holdPoint = point;
    }

    private void ReportArrived(float fromSpawn, string choice, bool zonesKnown)
    {
        if (arrivedReported)
        {
            return;
        }

        arrivedReported = true;
        var zonesNote = zonesKnown ? string.Empty : "；讀不到仇恨範圍（仇恨模組沒有載入），等待點只看出生點遠近";
        Svc.Log.Information($"{Tag} 到達：離出生點 {fromSpawn:F1} 米，{choice}{zonesNote}；{DescribeDue()}（出發後 {LedgerRules.Seconds(Environment.TickCount64 - departedAt)} 秒）");
    }

    // 讀不到仇恨範圍（模組沒載入）回 false，zones 為空＝每個點都算安全。
    private bool TryReadZones(out ReadOnlySpan<AggroZone> zones, out AggroState minState)
    {
        zones = default;
        minState = AggroState.Certain;
        if (!aggroResolved)
        {
            aggroResolved = true;
            module.TryGetModule(out aggro);
        }

        if (aggro == null)
        {
            return false;
        }

        var now = Environment.TickCount64;
        if (!aggro.Scanner.Snapshot.IsFresh(now, AggroSnapshotMaxAgeMs))
        {
            aggro.Scanner.Scan(aggro.BuildContext());
        }

        zoneBuffer ??= new AggroZone[AggroZoneSet.Capacity];
        var count = AggroZones.Build(aggro.Scanner.Snapshot.Entries, aggro.BuildZoneSettings(false), zoneBuffer);
        zones = zoneBuffer.AsSpan(0, count);
        minState = AggroZones.MinState(aggro.Config.AvoidPossible);
        return true;
    }

    // vnavmesh 查詢失敗＝那個點不能用。
    private bool TryPickPoint(ReadOnlySpan<AggroZone> zones, AggroState minState, out Vector3 point, out bool safe)
    {
        candidates ??= SnapCandidates();
        if (candidates == null)
        {
            point = default;
            safe = false;
            return false;
        }

        return PotWaitRules.TryPick(candidates, spawn, zones, minState, out point, out safe);
    }

    private Vector3?[]? SnapCandidates()
    {
        var snapped = new Vector3?[PotWaitRules.CandidateCount];
        var any = false;
        for (var i = 0; i < snapped.Length; i++)
        {
            var raw = PotWaitRules.Candidate(spawn, i);
            Vector3? point;
            try
            {
                point = vnav.FindNearestPointOnMesh(raw, SnapHalfExtentXZ, SnapHalfExtentY);
            }
            catch (Exception)
            {
                point = null;
            }

            if (PotWaitRules.AcceptSnap(raw, point, spawn))
            {
                snapped[i] = point;
                any = true;
            }
        }

        return any ? snapped : null;
    }

    /// <summary>從 PostUpdate 的失效路徑呼叫（同一個等待只寫一次）：出現了＝等到、推估落空＝落空，其他寫結束原因。</summary>
    public void ReportEnded()
    {
        if (endReported)
        {
            return;
        }

        endReported = true;
        var now = DateTime.UtcNow;
        var appeared = AppearedPot();
        var end = PotWaitRules.ClassifyEnd(module.Config, fates.potCycle.Cycle, fates.Config.GetPotCyclePeriod(), now, data.Id, appeared != 0);
        var waited = Waited();
        switch (end)
        {
            case PotWaitEnd.Appeared:
                ReportAppeared(appeared, waited);
                break;
            case PotWaitEnd.Missed:
                Svc.Log.Information($"{Tag} 落空：魔法甕「{potName}」推估 {Local(expectedUtc)} 出現，過了 {PotCycle.Grace.TotalMinutes:0} 分鐘仍沒出現（等了 {waited}）：不再等，照常選活動（下一次看到魔法甕出現前不知道何時出現）");
                break;
            case PotWaitEnd.SettingOff:
                Svc.Log.Information($"{Tag} 結束：設定改了（魔法甕快出現時先到出生點等、魔法甕優先，或「{potName}」的參加勾選）（等了 {waited}）：照常選活動");
                break;
            case PotWaitEnd.NotDue:
                Svc.Log.Information($"{Tag} 結束：推估魔法甕「{potName}」已不在出發窗內（{PotWaitRules.DepartureMinutes(module.Config)} 分鐘；等了 {waited}）：照常選活動");
                break;
            default:
                Svc.Log.Information($"{Tag} 結束：魔法甕推估沒了（換區或登出；等了 {waited}）");
                break;
        }
    }

    /// <summary>改去別的活動（Automator 換掉這個等待）時呼叫：魔法甕出現就寫「等到」，其他寫改去哪裡。</summary>
    public void ReportLeft(Activity next, string navigation)
    {
        if (endReported)
        {
            return;
        }

        endReported = true;
        var waited = Waited();
        if (next is FateActivity && PotCycle.IsPot(next.data.Id))
        {
            ReportAppeared(next.data.Id, waited);
            return;
        }

        Svc.Log.Information($"{Tag} 結束：有可接的{next.KindLabel}「{next.GetName()}」（{DescribeDue()}；等了 {waited}）：改去那裡；導航：{navigation}");
    }

    /// <summary>非法模式停止或換區（Automator.Refresh）時呼叫。</summary>
    public void ReportStopped(ActivityEnd end)
    {
        if (endReported)
        {
            return;
        }

        endReported = true;
        Svc.Log.Information($"{Tag} 結束：{LedgerRules.Label(end)}（等了 {Waited()}）");
    }

    private void ReportAppeared(uint appeared, string waited)
    {
        var distance = Svc.Objects.LocalPlayer is { } player ? $"{PotWaitRules.DistanceXZ(player.Position, spawn):F1} 米" : "?";
        var other = appeared == data.Id ? string.Empty : $"（推估的是「{potName}」）";
        var next = module.Config.TryGetFateEnabled(appeared, out var enabled) && enabled ? "照魔法甕優先前往參加" : "這個魔法甕沒有勾選參加，照常選活動";
        Svc.Log.Information($"{Tag} 等到：魔法甕「{fates.potCycle.GetName(appeared)}」出現{other}（等了 {waited}，角色離出生點 {distance}，換過 {repositions} 次等待點）：{next}");
    }

    // 在 F.A.T.E. 表上的魔法甕（推估的那個優先）；0＝都不在。
    private uint AppearedPot()
    {
        var table = fates.fates;
        if (table.ContainsKey(data.Id))
        {
            return data.Id;
        }

        var other = PotCycle.Opposite(data.Id);
        return table.ContainsKey(other) ? other : 0;
    }

    private string DescribeDue()
    {
        var remaining = expectedUtc - DateTime.UtcNow;
        return remaining >= TimeSpan.Zero
            ? $"推估魔法甕「{potName}」約 {PotCycleTracker.FormatDuration(remaining)} 後出現"
            : $"魔法甕「{potName}」的推估出現時間已過 {PotCycleTracker.FormatDuration(remaining.Negate())}、還沒看到";
    }

    /// <summary>出發時（Automator 選定這個等待的那一幀）呼叫一次。</summary>
    public void ReportDeparted(int windowMinutes)
    {
        var distance = Svc.Objects.LocalPlayer is { } player ? $"{PotWaitRules.DistanceXZ(player.Position, spawn):F1} 米" : "?";
        Svc.Log.Information($"{Tag} 出發：{DescribeDue()}，沒有別的活動可接（設定：魔法甕快出現時先到出生點等；出發窗 {windowMinutes} 分鐘）：先到出生點 {Format(spawn)} 附近等，離出生點 {distance}");
    }

    private string Waited()
    {
        return PotCycleTracker.FormatDuration(TimeSpan.FromMilliseconds(Environment.TickCount64 - departedAt));
    }

    private static string Clearance(float value)
    {
        return float.IsPositiveInfinity(value) ? "∞（附近沒有仇恨範圍）" : $"{value:F1} 米";
    }

    private static string Local(DateTime utc)
    {
        return utc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static string Format(Vector3 p)
    {
        return string.Create(CultureInfo.InvariantCulture, $"({p.X:F1}, {p.Y:F1}, {p.Z:F1})");
    }
}
