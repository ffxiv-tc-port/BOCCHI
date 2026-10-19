using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BOCCHI.Automation;
using BOCCHI.Data;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using Ocelot.IPC;

namespace BOCCHI.Modules.Aggro;

public enum AvoidanceStatus : byte
{
    Off = 0,
    Idle = 1,
    Combat = 2,
    Watching = 3,
    Planning = 4,
    Rerouted = 5,
    Unavoidable = 6,
    Suppressed = 7,
    EndpointMissing = 8,

    /// <summary>這一趟 30 秒內繞不開太多次：保持目前的路線（見 <see cref="ReplanGate"/>）。</summary>
    Holding = 9,

    /// <summary>繞不開，走 vnavmesh 給的穿越較淺的路線（見 <see cref="AvoidVerdict.Mitigated"/>）。</summary>
    Mitigated = 10,
}

/// <summary>
/// 趕路迴避的監看層：BOCCHI 自己登記的趕路、vnavmesh 正在走、路徑終點對得上、不在戰鬥（或戰鬥中仍騎乘，見 <see cref="AvoidanceRules.PausedByCombat"/>）時，
/// 每 250 ms 看前方 60 米有沒有穿過仇恨範圍，有就請 vnavmesh 繞路、驗過才換上。
/// 走繞路時當初要繞開的區域消失、移動或轉身，就再算一次，新路線短 5 米以上才換（見 <see cref="AvoidanceRules.Validate"/>）。
/// </summary>
/// <remarks>
/// 只在框架執行緒（AggroModule.Update）執行；vnavmesh 回傳的 Task 只輪詢 IsCompleted（丟棄時才掛一個只讀例外的接續）。
/// 不保存 IGameObject：玩家位置、目標 id 都在同一幀讀完。停止自動化（TravelIntent 作廢）時待處理的結果一律丟棄。
/// </remarks>
public sealed class AggroAvoidance
{
    private const long ZonesMaxAgeMs = 400;

    private const long EntryCheckIntervalMs = 100;

    private const long EndpointRetryMs = 30_000;

    private const long IpcResolveRetryMs = 5_000;

    private const long GiveWaySuppressMs = 10_000;

    // 進圈時，這麼久以內的規劃結果／沒送出的原因才算進圈原因。
    private const long OutcomeFreshMs = 3000;

    // 同一個沒送出的原因＋同一隻，Debug 最多這麼久寫一次。
    private const long HoldLogIntervalMs = 2000;

    private const long SpeedSampleMaxMs = 1000;

    private const float SpeedMax = 30f;

    private static readonly ConditionFlag[] PauseFlags =
    [
        ConditionFlag.Casting, ConditionFlag.Casting87, ConditionFlag.Mounting, ConditionFlag.Mounting71,
        ConditionFlag.OccupiedInEvent, ConditionFlag.OccupiedInQuestEvent, ConditionFlag.OccupiedInCutSceneEvent,
        ConditionFlag.Occupied, ConditionFlag.Occupied30, ConditionFlag.Occupied33, ConditionFlag.Occupied38, ConditionFlag.Occupied39,
        ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51,
    ];

    private readonly ReplanGate gate = new();

    private readonly ZoneSignature signature = new();

    private readonly StuckWatch stuck = new();

    private readonly byte[] exempt = new byte[AggroZoneSet.Capacity];

    private readonly bool[] hit = new bool[AggroZoneSet.Capacity];

    private readonly bool[] hitCertain = new bool[AggroZoneSet.Capacity];

    private readonly bool[] hitCurrent = new bool[AggroZoneSet.Capacity];

    private readonly float[] request = new float[AvoidanceRules.MaxRequestZones * AggroZones.ZoneStride];

    private HashSet<ulong> inside = new(64);

    private HashSet<ulong> insideNow = new(64);

    private VNavmesh? vnav;

    private VnavZoneIpc? zoneIpc;

    private long ipcResolveAt;

    private long nextCheckAt;

    private long nextEntryCheckAt;

    private long endpointRetryAt;

    private bool endpointLogged;

    private int tripSequence = -1;

    private bool plannedThisTrip;

    private bool appliedThisTrip;

    private bool unavoidableThisTrip;

    private bool firstEntryPass;

    private bool slowLogged;

    // 這一場戰鬥已寫過「騎乘中進入戰鬥：照常迴避」；脫離戰鬥時清掉。
    private bool ridingCombatLogged;

    // 最近一次 250 ms 檢查時，vnavmesh 正在走的是不是這一趟的路。
    private bool ownPath;

    // 最近一次 100 ms 檢查時走路穿越能不能用（能用時聽覺範圍不繞）；Check／HandleResult 照這個豁免。
    private bool walkExempt;

    private List<Vector3>? appliedPath;

    // appliedPath 是減傷換上的、而且還穿過範圍：之後只換穿越更少或更淺的（短 5 米但較深的不換）。appliedPath 清掉時一起清。
    private bool appliedMitigated;

    private Task<(List<Vector3> Path, int Flags, int Repaired, int Unavoidable)>? pending;

    private PendingRequest pendingInfo;

    private Vector3 speedSamplePosition;

    private long speedSampleAt = long.MinValue / 2;

    // X/Z 平面上的移動速度（米／秒），每次 250 ms 檢查量一次；量不到＝0。
    private float speed;

    private Outcome lastOutcome;

    private long lastOutcomeAt;

    private string lastFailReason = string.Empty;

    private Outcome lastHoldLogged;

    private ulong lastHoldZone;

    private long lastHoldLogAt = long.MinValue / 2;

    public AvoidanceStatus Status { get; private set; } = AvoidanceStatus.Idle;

    public int ReroutedCount { get; private set; }

    /// <summary>走繞路時重算、換上較短的路線的次數（不含在 <see cref="ReroutedCount"/> 裡）。</summary>
    public int ShortenedCount { get; private set; }

    public int UnavoidableCount { get; private set; }

    /// <summary>繞不開、改走穿越較淺的路線的次數（不含在 <see cref="UnavoidableCount"/> 裡）。</summary>
    public int MitigatedCount { get; private set; }

    public int GaveWayCount { get; private set; }

    /// <summary>這一拍對走路穿越的要求（每 100 ms 重算；AggroModule 交給 <see cref="WalkThrough"/>）。</summary>
    public WalkRequest Walk { get; private set; } = WalkRequest.Off(WalkStopReason.NotTravelling);

    public long SuppressedUntil
    {
        get => gate.SuppressedUntil;
    }

    public void Update(long now, AggroModule module)
    {
        var config = module.Config;
        if (!config.AvoidWhileMoving)
        {
            EndTrip(module);
            Walk = WalkRequest.Off(WalkStopReason.Disabled);
            Status = AvoidanceStatus.Off;
            return;
        }

        var state = TravelIntent.State;
        if (!state.HasIntent)
        {
            EndTrip(module);
            Walk = WalkRequest.Off(WalkStopReason.NotTravelling);
            Status = AvoidanceStatus.Idle;
            return;
        }

        var player = Svc.Objects.LocalPlayer;
        if (player == null || !state.TryGetActive(Svc.ClientState.TerritoryType, player.Position, now, out var intent))
        {
            EndTrip(module);
            Walk = WalkRequest.Off(WalkStopReason.NotTravelling);
            Status = AvoidanceStatus.Idle;
            return;
        }

        var position = player.Position;
        if (intent.Sequence != tripSequence)
        {
            StartTrip(intent, module);
        }

        var inCombat = Svc.Condition[ConditionFlag.InCombat];
        if (AvoidanceRules.PausedByCombat(inCombat, Svc.Condition[ConditionFlag.Mounted]))
        {
            DropPending();
            stuck.Stop();
            ownPath = false;
            Walk = WalkRequest.Off(WalkStopReason.Combat);
            Status = AvoidanceStatus.Combat;
            return;
        }

        if (!inCombat)
        {
            ridingCombatLogged = false;
        }
        else if (!ridingCombatLogged)
        {
            ridingCombatLogged = true;
            Log($"騎乘中進入戰鬥：照常迴避還沒發現你的魔物（已經進入戰鬥的魔物不算範圍）；下了坐騎才停止迴避（{intent.Source}）");
        }

        if (pending is { IsCompleted: true })
        {
            HandleResult(now, position, intent, module);
        }

        if (now >= nextEntryCheckAt)
        {
            nextEntryCheckAt = now + EntryCheckIntervalMs;
            var walkEnabled = module.Config.WalkThroughSound;
            var walkGate = WalkThroughRules.Gate(walkEnabled, inCombat, walkEnabled && !inCombat && module.Walker.Available(now));
            walkExempt = walkGate == WalkStopReason.None;
            Walk = EvaluateWalk(now, position, intent, module, walkGate);
            TrackEntries(now, position, intent, module);
        }

        if (now < nextCheckAt)
        {
            return;
        }

        nextCheckAt = now + AvoidanceRules.CheckIntervalMs;
        Check(now, position, intent, module);
    }

    private void StartTrip(in TravelIntentSnapshot intent, AggroModule module)
    {
        EndTrip(module);
        gate.ResetTrip();
        tripSequence = intent.Sequence;
        firstEntryPass = true;
        Status = AvoidanceStatus.Watching;
    }

    private void EndTrip(AggroModule module)
    {
        // 離線測試用未初始化的模組呼叫，Walker 可能是 null。
        module.Walker?.Toggle.ResetTrip();
        walkExempt = false;
        tripSequence = -1;
        DropPending();
        appliedPath = null;
        appliedMitigated = false;
        plannedThisTrip = appliedThisTrip = unavoidableThisTrip = ownPath = false;
        lastOutcome = Outcome.None;
        stuck.Stop();
        signature.Clear();
        inside.Clear();
    }

    private void Check(long now, Vector3 position, in TravelIntentSnapshot intent, AggroModule module)
    {
        SampleSpeed(now, position);
        var slow = gate.IsSlow(now);
        if (!slow)
        {
            slowLogged = false;
        }

        if (gate.IsSuppressed(now))
        {
            Status = AvoidanceStatus.Suppressed;
        }
        else if (Status is AvoidanceStatus.Suppressed or AvoidanceStatus.Combat || (Status == AvoidanceStatus.EndpointMissing && now >= endpointRetryAt)
                 || (Status == AvoidanceStatus.Holding && !slow))
        {
            Status = RouteStatus();
        }

        if (!ResolveIpc(now, module))
        {
            return;
        }

        bool running;
        try
        {
            running = vnav!.IsRunning();
        }
        catch (Exception)
        {
            return;
        }

        if (!running)
        {
            ownPath = false;
            stuck.Stop();
            return;
        }

        var zones = module.Zones;
        var look = AvoidanceRules.ShouldLook(zones.IsFresh(now, ZonesMaxAgeMs), zones.Count, signature.Count);
        if (!look && !stuck.Active)
        {
            return;
        }

        if (!TryReadPath(intent, out var waypoints))
        {
            ownPath = false;
            stuck.Stop();
            return;
        }

        ownPath = true;
        var path = CollectionsMarshal.AsSpan(waypoints);
        if (appliedPath != null && !AvoidanceRules.IsSuffixOf(path, CollectionsMarshal.AsSpan(appliedPath)))
        {
            // 別人換了路（例如重新下尋路）：上次規劃的那組區域不再代表目前的路，卡住偵測也不再是我們的繞路。
            appliedPath = null;
            appliedMitigated = false;
            stuck.Stop();
            signature.Clear();
        }

        if (stuck.Active && stuck.Update(now, position, IsPaused()) == StuckState.Stuck)
        {
            GiveWay(now, intent);
            return;
        }

        if (!look)
        {
            return;
        }

        var list = zones.Zones;
        var end = path[^1];
        AvoidanceRules.MarkExempt(list, position, end, TargetId(), walkExempt, exempt);
        var minState = AggroZones.MinState(module.Config.AvoidPossible);
        var crossings = AggroZones.CountCrossings(list, exempt, minState, position, path, AvoidanceRules.HorizonM, hit);
        PlanKind kind;
        if (pending != null)
        {
            // 只為了診斷：結果回來前不送下一次。
            if (AvoidanceRules.TryChooseKind(crossings, plannedThisTrip, appliedPath != null, signature, list, hit, out kind, out _) && kind != PlanKind.Recheck)
            {
                Hold(now, position, path, list, minState, crossings, Outcome.Pending, intent, module);
            }

            return;
        }

        if (gate.IsSuppressed(now))
        {
            if (crossings > 0)
            {
                Hold(now, position, path, list, minState, crossings, Outcome.Suppressed, intent, module);
            }

            return;
        }

        signature.Prune(position, path, AvoidanceRules.HorizonM);
        if (!AvoidanceRules.TryChooseKind(crossings, plannedThisTrip, appliedPath != null, signature, list, hit, out kind, out var change))
        {
            if (crossings > 0)
            {
                Hold(now, position, path, list, minState, crossings, Outcome.Unchanged, intent, module);
            }

            return;
        }

        if (now < endpointRetryAt)
        {
            Status = AvoidanceStatus.EndpointMissing;
            if (kind != PlanKind.Recheck)
            {
                Hold(now, position, path, list, minState, crossings, Outcome.Endpoint, intent, module);
            }

            return;
        }

        var urgent = false;
        var decision = gate.Evaluate(now, kind);
        if (kind != PlanKind.Recheck && decision is (ReplanDecision.TooSoon or ReplanDecision.RateLimited)
            && AvoidanceRules.FirstCrossingDistance(list, exempt, AggroState.Certain, position, path, AvoidanceRules.UrgentDistance(speed), out _) < float.PositiveInfinity)
        {
            urgent = true;
            decision = gate.Evaluate(now, kind, true);
        }

        switch (decision)
        {
            case ReplanDecision.Plan:
                break;
            case ReplanDecision.RateLimited:
                if (kind != PlanKind.Recheck)
                {
                    Status = AvoidanceStatus.Holding;
                    Hold(now, position, path, list, minState, crossings, Outcome.RateLimited, intent, module);
                }

                if (gate.IsSlow(now) && !slowLogged)
                {
                    slowLogged = true;
                    Log($"重算太頻繁：這一趟 {ReplanGate.RateWindowMs / 1000} 秒內已 {ReplanGate.RateMax} 次繞不開，保持目前的路線；"
                        + $"路線新穿過範圍時最多每 {ReplanGate.SlowIntervalMs / 1000} 秒重算一次（{AvoidanceRules.UrgentSeconds:f1} 秒內會進確定範圍的不受限），暫停「繞路後重算改短」（{intent.Source}）");
                }

                return;
            default:
                if (kind != PlanKind.Recheck)
                {
                    Hold(now, position, path, list, minState, crossings, Outcome.TooSoon, intent, module);
                }

                return;
        }

        var certain = minState == AggroState.Certain
            ? crossings
            : AggroZones.CountCrossings(list, exempt, AggroState.Certain, position, path, AvoidanceRules.HorizonM, hitCertain);
        var again = kind == PlanKind.Crossing && signature.Recrossed(list, hit);
        gate.RecordPlan(now, kind);
        plannedThisTrip = true;
        signature.Merge(list, hit);
        Send(now, position, intent, end, list, hit, minState, crossings, certain, AggroZones.PathLength(position, path), kind, change, urgent, false, again);
    }

    // crossings／crossed＝這一段（minState）原本那條路的穿越數與是哪幾個；certain＝其中確定的穿越數（第二段退讓用）。
    private void Send(long now, Vector3 position, in TravelIntentSnapshot intent, Vector3 end, ReadOnlySpan<AggroZone> zones, ReadOnlySpan<bool> crossed, AggroState minState, int crossings, int certain, float length,
        PlanKind kind, SignatureChange change, bool urgent, bool afterMitigated = false, bool again = false)
    {
        var packed = AvoidanceRules.PackRequest(zones, exempt, crossed, minState, request);
        try
        {
            pending = zoneIpc!.PathfindAroundZones(position, end, request, AvoidanceRules.HorizonM);
        }
        catch (Exception ex)
        {
            pending = null;
            endpointRetryAt = now + EndpointRetryMs;
            Status = AvoidanceStatus.EndpointMissing;
            SetOutcome(now, Outcome.Endpoint);
            if (!endpointLogged)
            {
                endpointLogged = true;
                Log($"端點不可用：vnavmesh 沒有 Nav.PathfindAroundZones（版本太舊或沒載入），照原路走、不繞開仇恨範圍（{ex.GetType().Name}：{ex.Message}）");
            }

            return;
        }

        pendingInfo = new PendingRequest(intent, minState, end, length, crossings, certain, packed, kind, change, urgent, afterMitigated, again);
        if (kind != PlanKind.Recheck && !afterMitigated)
        {
            Status = AvoidanceStatus.Planning;
            SetOutcome(now, Outcome.Sent);
        }
    }

    private void HandleResult(long now, Vector3 position, in TravelIntentSnapshot intent, AggroModule module)
    {
        var task = pending!;
        var info = pendingInfo;
        pending = null;
        var recheck = info.Kind == PlanKind.Recheck;
        // 減傷路線已換上之後的第二段：沒有更好的就留在減傷路線，不寫繞不開、不清名單。
        var keep = recheck || info.AfterMitigated;
        var error = task.IsFaulted ? task.Exception?.GetBaseException() : null;
        if (!TravelIntent.State.IsCurrent(info.Intent) || task.IsCanceled)
        {
            // 被取消（停止、網格重載）或換了一趟：這次的區域組合不算規劃過，下一次檢查可以重來。
            DropResult(keep);
            return;
        }

        if (task.IsFaulted)
        {
            if (keep)
            {
                Svc.Log.Debug($"[BOCCHI][仇恨迴避] {(recheck ? "重算" : "第二段（只避確定）")}：vnavmesh 算路失敗，保持目前的路線（{error?.GetType().Name}：{error?.Message}）");
                return;
            }

            Fallback(now, intent, info, $"vnavmesh 算路失敗（{error?.GetType().Name}：{error?.Message}）", true, 0, -1, info.Crossings);
            return;
        }

        var (path, flags, repaired, unavoidable) = task.Result;
        try
        {
            if (vnav!.IsSimpleMoveInProgress() || !vnav.IsRunning())
            {
                DropResult(keep);
                return;
            }
        }
        catch (Exception)
        {
            return;
        }

        // 兩條路都在結果回來這一刻、從玩家目前的位置量：算路期間玩家已經走了一段。
        if (!TryReadPath(intent, out var waypoints))
        {
            DropResult(keep);
            return;
        }

        var zones = module.Zones.Zones;
        var current = CollectionsMarshal.AsSpan(waypoints);
        AvoidanceRules.MarkExempt(zones, position, info.ExpectedEnd, TargetId(), walkExempt, exempt);
        var currentCrossings = AggroZones.CountCrossings(zones, exempt, info.MinState, position, current, AvoidanceRules.HorizonM, hitCurrent);
        var currentLength = AggroZones.PathLength(position, current);
        var full = CollectionsMarshal.AsSpan(path);
        var trimmed = AvoidanceRules.TrimStart(full, position);
        var newCrossings = trimmed.Length == 0 ? int.MaxValue : AggroZones.CountCrossings(zones, exempt, info.MinState, position, trimmed, AvoidanceRules.HorizonM, hit);
        var newLength = AggroZones.PathLength(position, trimmed);
        var exposure = ExposureChange.Same;
        RouteExposure currentExposure = default, newExposure = default;
        if (newCrossings == currentCrossings && currentCrossings > 0 && ((flags & VnavZoneFlags.Mitigated) != 0 || appliedMitigated))
        {
            exposure = AvoidanceRules.CompareExposure(zones, hitCurrent, hit, position, current, trimmed, out currentExposure, out newExposure);
        }

        var verdict = AvoidanceRules.Validate(flags, full, position, info.ExpectedEnd, newCrossings, currentCrossings, newLength, currentLength, exposure, appliedMitigated);
        if (verdict is AvoidVerdict.Accept or AvoidVerdict.Mitigated)
        {
            // 名單用 Check 的門檻看新路線還穿過誰：第二段只數確定的，會把仍穿過的「可能」誤記成已繞開。
            var broad = AggroZones.MinState(module.Config.AvoidPossible);
            if (broad != info.MinState)
            {
                AggroZones.CountCrossings(zones, exempt, broad, position, trimmed, AvoidanceRules.HorizonM, hit);
            }

            var move = trimmed.Length == full.Length ? path : path.GetRange(1, path.Count - 1);
            try
            {
                vnav.MoveTo(move, false);
            }
            catch (Exception ex)
            {
                Log($"規劃：繞路算好了但交給 vnavmesh 失敗，照目前的路線走（{ex.GetType().Name}：{ex.Message}）");
                return;
            }

            signature.MarkCleared(zones, hit);
            var mitigated = verdict == AvoidVerdict.Mitigated;
            appliedMitigated = newCrossings > 0 && (mitigated || (flags & VnavZoneFlags.Mitigated) != 0 || appliedMitigated);
            appliedPath = move;
            appliedThisTrip = true;
            stuck.Start(now, position);
            Status = RouteStatus();
            var delta = newLength - currentLength;
            var lengthText = $"路長 {currentLength:f1} → {newLength:f1} 米（{(delta >= 0 ? "多走" : "少走")} {MathF.Abs(delta):f1} 米），修補 {repaired} 段，旗標 0x{flags:X}（{intent.Source}）";
            if (mitigated)
            {
                MitigatedCount++;
                var depth = $"前方 {AvoidanceRules.HorizonM:f0} 米內穿越 {newCrossings}，區域內 {currentExposure.InsideM:f1} → {newExposure.InsideM:f1} 米、"
                            + $"離怪最近 {NearText(currentExposure.NearestM)} → {NearText(newExposure.NearestM)} 米";
                if (recheck)
                {
                    Log($"重算：換上穿越較淺的路線（{ChangeText(info.Change)}），{depth}，{lengthText}");
                    return;
                }

                // 仍穿過範圍＝繞不開，照樣算進「重算太頻繁」的預算。
                gate.RecordFailure(now);
                SetOutcome(now, Outcome.Mitigated);
                Log($"規劃：避不開，換上穿越較淺的路線（{KindName(info.Kind, info.Again)}{(info.Urgent ? "，緊急" : "")}，{StageName(info.MinState)}，送出 {info.Packed} 個範圍），{depth}，{lengthText}");
                if (!info.AfterMitigated && AvoidanceRules.ShouldRetryCertainOnly(info.MinState, info.CertainCrossings))
                {
                    TryCertainAfterMitigated(now, position, intent, zones, trimmed, newLength, info);
                }

                return;
            }

            SetOutcome(now, Outcome.Applied);
            if (recheck)
            {
                ShortenedCount++;
                Log($"重算：換上較短的路線（{ChangeText(info.Change)}），前方 {AvoidanceRules.HorizonM:f0} 米內穿越 {currentCrossings} → {newCrossings}，{lengthText}");
                return;
            }

            ReroutedCount++;
            Log($"規劃：換上繞路（{KindName(info.Kind, info.Again)}{(info.Urgent ? "，緊急" : "")}，{StageName(info.MinState)}，送出 {info.Packed} 個範圍），前方 {AvoidanceRules.HorizonM:f0} 米內穿越 {currentCrossings} → {newCrossings}，{lengthText}");
            return;
        }

        if (keep)
        {
            var head = recheck ? $"重算：保持目前的路線（{ChangeText(info.Change)}；" : "第二段（只避確定）沒有更好的：保持穿越較淺的路線（";
            Svc.Log.Debug($"[BOCCHI][仇恨迴避] {head}{VerdictText(verdict)}；穿越 {currentCrossings} → {newCrossings}、路長 {currentLength:f1} → {newLength:f1} 米）");
            return;
        }

        if (AvoidanceRules.ShouldRetryCertainOnly(info.MinState, info.CertainCrossings))
        {
            var certain = AggroZones.CountCrossings(zones, exempt, AggroState.Certain, position, current, AvoidanceRules.HorizonM, hitCertain);
            if (certain > 0)
            {
                Send(now, position, intent, current[^1], zones, hitCertain, AggroState.Certain, certain, certain, currentLength, info.Kind, info.Change, info.Urgent, false, info.Again);
                if (pending != null)
                {
                    return;
                }
            }
        }

        // 送出那一刻有穿越、結果回來時目前的路線已經不穿越（NotShorter）：不算繞不開的預算。
        Fallback(now, intent, info, VerdictText(verdict), verdict != AvoidVerdict.NotShorter, flags, unavoidable, currentCrossings);
    }

    // 第一段（確定＋可能）換上減傷路線後仍穿過確定範圍：只用確定的範圍再試一次，要比減傷路線穿越更少或更淺才換（Validate 的 keepShallow）。
    private void TryCertainAfterMitigated(long now, Vector3 position, in TravelIntentSnapshot intent, ReadOnlySpan<AggroZone> zones, ReadOnlySpan<Vector3> applied, float length,
        in PendingRequest info)
    {
        var certain = AggroZones.CountCrossings(zones, exempt, AggroState.Certain, position, applied, AvoidanceRules.HorizonM, hitCertain);
        if (certain > 0)
        {
            Send(now, position, intent, info.ExpectedEnd, zones, hitCertain, AggroState.Certain, certain, certain, length, info.Kind, info.Change, info.Urgent, true, info.Again);
        }
    }

    private AvoidanceStatus RouteStatus()
    {
        return appliedPath == null ? AvoidanceStatus.Watching : appliedMitigated ? AvoidanceStatus.Mitigated : AvoidanceStatus.Rerouted;
    }

    private static string NearText(float nearest)
    {
        return float.IsPositiveInfinity(nearest) ? "沒進入" : nearest.ToString("f1");
    }

    // 結果作廢：繞路請求的那組區域不算規劃過（下一次檢查可以重來）；重算改短的不動名單。
    private void DropResult(bool recheck)
    {
        if (!recheck)
        {
            signature.Clear();
        }

        Status = RouteStatus();
    }

    private void Fallback(long now, in TravelIntentSnapshot intent, in PendingRequest info, string reason, bool failure, int flags, int unavoidable, int crossings)
    {
        unavoidableThisTrip = true;
        UnavoidableCount++;
        // 正走在減傷路線上時「照目前的路線走」就是留在減傷路線。
        var onMitigated = appliedPath != null && appliedMitigated;
        Status = onMitigated ? AvoidanceStatus.Mitigated : AvoidanceStatus.Unavoidable;
        lastFailReason = onMitigated ? reason + "，留在穿越較淺的路線" : reason;
        SetOutcome(now, Outcome.Failed);
        if (failure)
        {
            gate.RecordFailure(now);
        }

        Log($"規劃：繞不開，照目前的路線走（{lastFailReason}；{KindName(info.Kind, info.Again)}，最後一段{StageName(info.MinState)}，前方穿越 {crossings}，vnavmesh 避不開 {unavoidable}、旗標 0x{flags:X}）（{intent.Source}）");
    }

    private void GiveWay(long now, in TravelIntentSnapshot intent)
    {
        appliedPath = null;
        appliedMitigated = false;
        gate.Suppress(now, GiveWaySuppressMs);
        GaveWayCount++;
        Status = AvoidanceStatus.Suppressed;
        try
        {
            vnav!.PathfindAndMoveTo(intent.Destination, false);
        }
        catch (Exception)
        {
            // 呼叫失敗時 vnavmesh 照舊走目前那條繞路；抑制期間不再換路。
        }

        Log($"退回原路：換上繞路後 {StuckWatch.WindowMs / 1000} 秒內移動不到 {StuckWatch.MinMoveM} 米（施法、互動、上坐騎的時間不算），"
            + $"重新照原本的路線前往，{GiveWaySuppressMs / 1000} 秒內不迴避（{intent.Source}）");
    }

    // gate＝這一拍 WalkThroughRules.Gate 的結果（騎乘中也走路穿越；戰鬥中不走）。
    private WalkRequest EvaluateWalk(long now, Vector3 position, in TravelIntentSnapshot intent, AggroModule module, WalkStopReason gate)
    {
        if (gate != WalkStopReason.None)
        {
            return WalkRequest.Off(gate);
        }

        var zones = module.Zones;
        if (!ownPath || vnav == null || !zones.IsFresh(now, ZonesMaxAgeMs))
        {
            return WalkRequest.Off(WalkStopReason.NotMoving);
        }

        var list = zones.Zones;
        var minState = AggroZones.MinState(module.Config.AvoidPossible);
        var targetId = TargetId();
        var hold = module.Walker.Toggle.Owned ? WalkThroughRules.HoldM : 0f;
        var lookahead = WalkThroughRules.Lookahead(speed);
        if (!WalkThroughRules.AnyNear(list, minState, position, targetId, MathF.Max(lookahead, hold)))
        {
            return WalkRequest.Off(WalkStopReason.Passed);
        }

        if (!TryReadPath(intent, out var waypoints))
        {
            return WalkRequest.Off(WalkStopReason.NotMoving);
        }

        var index = WalkThroughRules.Find(list, minState, position, CollectionsMarshal.AsSpan(waypoints), targetId, lookahead, hold);
        if (index < 0)
        {
            return WalkRequest.Off(WalkStopReason.Passed);
        }

        var zone = list[index];
        uint nameId = 0;
        var level = 0;
        foreach (ref readonly var entry in module.Scanner.Snapshot.Entries)
        {
            if (entry.GameObjectId == zone.GameObjectId)
            {
                nameId = entry.NameId;
                level = entry.Level;
                break;
            }
        }

        var dx = position.X - zone.Center.X;
        var dz = position.Z - zone.Center.Z;
        return new WalkRequest(true, WalkStopReason.None, zone.GameObjectId, nameId, level, MathF.Sqrt(dx * dx + dz * dz), zone.Radius);
    }

    private void TrackEntries(long now, Vector3 position, in TravelIntentSnapshot intent, AggroModule module)
    {
        // vnavmesh 走的不是這一趟的路（還在算、或在追目標）時不記也不更新：開始走的那一刻才算「出發」。
        var zones = module.Zones;
        if (!ownPath || !zones.IsFresh(now, ZonesMaxAgeMs))
        {
            return;
        }

        insideNow.Clear();
        var list = zones.Zones;
        for (var i = 0; i < list.Length; i++)
        {
            if (AggroZones.Contains(list[i], position) && insideNow.Add(list[i].GameObjectId) && !inside.Contains(list[i].GameObjectId))
            {
                LogEntry(now, position, intent, list[i], module);
            }
        }

        (inside, insideNow) = (insideNow, inside);
        firstEntryPass = false;
    }

    private void LogEntry(long now, Vector3 position, in TravelIntentSnapshot intent, in AggroZone zone, AggroModule module)
    {
        var minState = AggroZones.MinState(module.Config.AvoidPossible);
        var destination = intent.Destination;
        var targetId = TargetId();
        var reason = firstEntryPass ? "已在範圍內出發"
            : walkExempt && WalkThroughRules.IsWalkable(zone) ? (module.Walker.LastWalking ? "走路穿越（走路中）" : "走路穿越（還沒切成走路）")
            : AggroZones.InBand(zone, destination.Y) && AggroZones.Distance2D(zone, destination) <= AvoidanceRules.DestinationExemptM ? "終點豁免"
            : targetId != 0 && zone.GameObjectId == targetId ? "目前目標"
            : zone.State < minState ? "只避確定的範圍"
            : lastOutcome != Outcome.None && now - lastOutcomeAt <= OutcomeFreshMs ? OutcomeText(lastOutcome)
            : now < endpointRetryAt ? "vnavmesh 不支援繞路"
            : gate.IsSuppressed(now) ? "抑制中"
            : gate.IsSlow(now) ? "重算太頻繁（保持目前的路線）"
            : appliedPath != null && appliedMitigated ? "減傷路線（避不開，走穿越較淺的路線）"
            : unavoidableThisTrip ? "避不開"
            : appliedThisTrip ? "已規劃仍進入"
            : "沒有事先規劃（範圍突然出現或檢查還沒輪到）";

        FindEntry(module, zone.GameObjectId, out var nameId, out var level);
        var name = Svc.Data.GetExcelSheet<BNpcName>().GetRowOrDefault(nameId)?.Singular.ExtractText() ?? "?";
        var dx = position.X - zone.Center.X;
        var dz = position.Z - zone.Center.Z;
        Log($"進圈：{name}（NameId {nameId}、Lv{(level > 0 ? level.ToString() : "?")}、{SenseName(zone.Sense)}、{(zone.State == AggroState.Certain ? "確定" : "可能")}），"
            + $"距離 {MathF.Sqrt(dx * dx + dz * dz):f1} 米／範圍 {zone.Radius:f1} 米，原因＝{reason}（{intent.Source}）");
    }

    private void SetOutcome(long now, Outcome outcome)
    {
        lastOutcome = outcome;
        lastOutcomeAt = now;
    }

    // 前方有穿越卻沒送出規劃：記下原因（進圈時用；Unchanged 不蓋掉上一次的結果），Debug 寫一行（同原因＋同一隻每 HoldLogIntervalMs 一次）。
    private void Hold(long now, Vector3 position, ReadOnlySpan<Vector3> path, ReadOnlySpan<AggroZone> zones, AggroState minState, int crossings, Outcome reason,
        in TravelIntentSnapshot intent, AggroModule module)
    {
        if (reason != Outcome.Unchanged)
        {
            SetOutcome(now, reason);
        }

        var distance = AvoidanceRules.FirstCrossingDistance(zones, exempt, minState, position, path, AvoidanceRules.HorizonM, out var index);
        if (index < 0)
        {
            return;
        }

        var zone = zones[index];
        if (reason == lastHoldLogged && zone.GameObjectId == lastHoldZone && now - lastHoldLogAt < HoldLogIntervalMs)
        {
            return;
        }

        lastHoldLogged = reason;
        lastHoldZone = zone.GameObjectId;
        lastHoldLogAt = now;
        FindEntry(module, zone.GameObjectId, out var nameId, out _);
        var name = Svc.Data.GetExcelSheet<BNpcName>().GetRowOrDefault(nameId)?.Singular.ExtractText() ?? "?";
        var detail = reason switch
        {
            Outcome.Unchanged => $"穿過的範圍跟上次規劃時一樣（同一隻移動不到 {ZoneSignature.MoveM:f0} 米、轉身不到 {ZoneSignature.TurnDegrees:f0}°），不重算；"
                                 + $"上次＝{(lastOutcome == Outcome.None ? "?" : OutcomeText(lastOutcome))}",
            Outcome.TooSoon => $"離上次穿越規劃 {(now - gate.LastCrossingAt) / 1000f:f2} 秒（最短間隔 {ReplanGate.MinIntervalMs / 1000f:f1} 秒；"
                               + $"這一趟第一次、或 {AvoidanceRules.UrgentSeconds:f1} 秒內會進確定範圍的只要 {ReplanGate.FloorMs / 1000f:f1} 秒）",
            Outcome.RateLimited => $"重算太頻繁（這一趟 {ReplanGate.RateWindowMs / 1000} 秒內繞不開 {gate.FailuresWithin(now)} 次，每 {ReplanGate.SlowIntervalMs / 1000} 秒重算一次）",
            _ => OutcomeText(reason),
        };
        var eta = speed >= 1f ? $"約 {distance / speed:f1} 秒後" : "還量不到速度，不知道幾秒後";
        Svc.Log.Debug($"[BOCCHI][仇恨迴避] 前方穿越沒送出規劃：{detail}；最先碰到 {name}（{SenseName(zone.Sense)}、{(zone.State == AggroState.Certain ? "確定" : "可能")}），"
                      + $"沿路 {distance:f1} 米、{eta}進圈（速度 {speed:f1} 米／秒），前方穿越 {crossings}（{intent.Source}）");
    }

    private void SampleSpeed(long now, Vector3 position)
    {
        var dt = now - speedSampleAt;
        if (dt > 0 && dt <= SpeedSampleMaxMs)
        {
            var dx = position.X - speedSamplePosition.X;
            var dz = position.Z - speedSamplePosition.Z;
            speed = MathF.Min(MathF.Sqrt(dx * dx + dz * dz) * 1000f / dt, SpeedMax);
        }
        else
        {
            speed = 0f;
        }

        speedSampleAt = now;
        speedSamplePosition = position;
    }

    private static void FindEntry(AggroModule module, ulong gameObjectId, out uint nameId, out int level)
    {
        nameId = 0;
        level = 0;
        foreach (ref readonly var entry in module.Scanner.Snapshot.Entries)
        {
            if (entry.GameObjectId == gameObjectId)
            {
                nameId = entry.NameId;
                level = entry.Level;
                return;
            }
        }
    }

    private string OutcomeText(Outcome outcome)
    {
        return outcome switch
        {
            Outcome.Sent => "已送出規劃、結果還沒回來",
            Outcome.Applied => "已規劃仍進入",
            Outcome.Mitigated => "減傷路線（避不開，剛換上穿越較淺的路線）",
            Outcome.Failed => $"避不開（{lastFailReason}）",
            Outcome.Pending => "等上一次規劃的結果",
            Outcome.Suppressed => "抑制中",
            Outcome.Endpoint => "vnavmesh 不支援繞路",
            Outcome.TooSoon => $"離上次穿越規劃未滿 {ReplanGate.MinIntervalMs / 1000f:f1} 秒",
            Outcome.RateLimited => "重算太頻繁（保持目前的路線）",
            Outcome.Unchanged => "穿過的範圍跟上次規劃時一樣，不重算",
            _ => "?",
        };
    }

    // 丟掉還沒回來的結果；之後才失敗的也要讀一次 Exception，否則 Dalamud 會記一行「Unobserved exception in Task」。
    private void DropPending()
    {
        var task = pending;
        pending = null;
        if (task == null)
        {
            return;
        }

        if (!task.IsCompleted)
        {
            task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        else if (task.IsFaulted)
        {
            _ = task.Exception;
        }
    }

    private bool TryReadPath(in TravelIntentSnapshot intent, out List<Vector3> waypoints)
    {
        try
        {
            waypoints = vnav!.GetWaypoints();
        }
        catch (Exception)
        {
            waypoints = null!;
            return false;
        }

        return waypoints is { Count: > 0 } && AvoidanceRules.IsOwnPath(waypoints[^1], intent.Destination);
    }

    private bool ResolveIpc(long now, AggroModule module)
    {
        if (vnav != null && zoneIpc != null)
        {
            return true;
        }

        if (now < ipcResolveAt)
        {
            return false;
        }

        ipcResolveAt = now + IpcResolveRetryMs;
        module.TryGetIPCSubscriber(out vnav);
        module.TryGetIPCSubscriber(out zoneIpc);
        return vnav != null && zoneIpc != null;
    }

    private static bool IsPaused()
    {
        var condition = Svc.Condition;
        foreach (var flag in PauseFlags)
        {
            if (condition[flag])
            {
                return true;
            }
        }

        return false;
    }

    private static ulong TargetId()
    {
        return Svc.Targets.Target?.GameObjectId ?? 0;
    }

    private static string StageName(AggroState minState)
    {
        return minState == AggroState.Certain ? "只避確定" : "確定＋可能";
    }

    private static string SenseName(AggroSense sense)
    {
        return sense switch
        {
            AggroSense.Sight => "視覺",
            AggroSense.Sound => "聽覺",
            AggroSense.Proximity => "範圍",
            AggroSense.Magic => "魔法",
            AggroSense.Blood => "血量",
            _ => "未知",
        };
    }

    private static string VerdictText(AvoidVerdict verdict)
    {
        return verdict switch
        {
            AvoidVerdict.NoPath => "找不到路",
            AvoidVerdict.EmptyPath => "路徑是空的",
            AvoidVerdict.ClientPathLinks => "路徑含客戶端固定路線，不能直接換",
            AvoidVerdict.EndFar => "新路徑的終點離原本的終點超過 3 米",
            AvoidVerdict.StartFar => "新路徑的起點離你超過 5 米",
            AvoidVerdict.NotFewer => "新路徑穿過的範圍沒有比較少",
            AvoidVerdict.TooLong => "繞太遠（比目前的路線多走超過 15 米，且超過目前路長的 25%）",
            AvoidVerdict.NotShorter => $"新路線沒有短 {AvoidanceRules.ShorterGainM:f0} 米以上",
            AvoidVerdict.Deeper => "新路線較短但穿越較深（目前走的是穿越較淺的路線）",
            _ => verdict.ToString(),
        };
    }

    private static string KindName(PlanKind kind, bool again)
    {
        return kind switch
        {
            PlanKind.First => "這一趟第一次",
            PlanKind.Crossing => again ? "上次繞開的範圍又擋在路上" : "路線新穿過範圍",
            _ => "重算",
        };
    }

    private static string ChangeText(SignatureChange change)
    {
        return change switch
        {
            SignatureChange.Gone => "要繞開的魔物已不在範圍清單上（進入戰鬥、死亡或走遠）",
            SignatureChange.Moved => $"要繞開的魔物移動了 {ZoneSignature.MoveM:f0} 米以上",
            SignatureChange.Turned => $"要繞開的魔物轉身超過 {ZoneSignature.TurnDegrees:f0}°",
            _ => "?",
        };
    }

    private static void Log(string message)
    {
        Svc.Log.Information($"[BOCCHI][仇恨迴避] {message}");
    }

    // 最近一次穿越規劃的結果，或前方有穿越卻沒送出的原因。
    private enum Outcome : byte
    {
        None = 0,
        Sent = 1,
        Applied = 2,
        Failed = 3,
        Pending = 4,
        Suppressed = 5,
        Endpoint = 6,
        TooSoon = 7,
        RateLimited = 8,
        Unchanged = 9,
        Mitigated = 10,
    }

    private readonly record struct PendingRequest(
        TravelIntentSnapshot Intent,
        AggroState MinState,
        Vector3 ExpectedEnd,
        float Length,
        int Crossings,
        int CertainCrossings,
        int Packed,
        PlanKind Kind,
        SignatureChange Change,
        bool Urgent,
        bool AfterMitigated,
        bool Again);
}
