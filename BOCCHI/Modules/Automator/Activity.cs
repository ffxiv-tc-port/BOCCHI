using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BOCCHI.Automation;
using BOCCHI.Chains;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Ledger;
using BOCCHI.Modules.StateManager;
using BOCCHI.Pathfinding;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using Ocelot.Chain;
using Ocelot.IPC;

namespace BOCCHI.Modules.Automator;

public abstract partial class Activity
{
    public readonly EventData data;

    private readonly Lifestream lifestream;

    private LifestreamBusyProbe? lifestreamBusy;

    private HandsOnProbe? handsOn;

    private const long TravelHoldFreshMs = 500;

    // 趕路監看最近一拍的 TravelStopGuard.HoldsOwnMoves 與那一拍的時間；監看結束後過 TravelHoldFreshMs 就不算。
    private bool travelHolding;

    private long travelHoldingAt;

    protected readonly VNavmesh vnav;

    protected readonly AutomatorModule module;

    /// <summary>這個活動改寫目標的入口（參加中與趕路補目標共用，才分得出哪一隻是 BOCCHI 自己選的）。</summary>
    protected readonly TargetKeeper targets = new();

    public ActivityState state = ActivityState.Idle;

    private ActivityRecord? record;

    /// <summary>時間軸與計數（活動帳用）；只在 framework 執行緒讀寫。不經建構子建出的物件第一次讀時才建。</summary>
    public ActivityRecord Record
    {
        get => record ??= new ActivityRecord { SelectedAt = Environment.TickCount64 };
    }

    protected readonly Dictionary<ActivityState, Func<StateManagerModule, Func<Chain>?>> handlers;

    protected Activity(EventData data, Lifestream lifestream, VNavmesh vnav, AutomatorModule module)
    {
        this.data = data;
        this.lifestream = lifestream;
        this.vnav = vnav;
        this.module = module;
        record = new ActivityRecord { SelectedAt = Environment.TickCount64 };
        targets.Source = $"{KindLabel}#{data.Id}";

        handlers = new Dictionary<ActivityState, Func<StateManagerModule, Func<Chain>?>>
        {
            { ActivityState.Idle, GetIdleChain },
            { ActivityState.Pathfinding, GetPathfindingChain },
            { ActivityState.Participating, GetParticipatingChain },
            { ActivityState.Done, GetDoneChain },
        };

        var states = module.GetModule<StateManagerModule>();
        if (states.GetState() == State.InFate || states.GetState() == State.InCriticalEncounter)
        {
            state = ActivityState.Participating;
            Record.MarkParticipating(Record.SelectedAt, -1);
        }
    }

    public Func<Chain>? GetChain(StateManagerModule states)
    {
        return !IsValid() ? null : handlers[state](states);
    }

    private Func<Chain> GetIdleChain(StateManagerModule states)
    {
        return () =>
        {
            bool ShouldToggleAi(ChainContext _)
            {
                return module.Config.ShouldToggleAiProvider && !Svc.Condition[ConditionFlag.InCombat];
            }

            return Chain.Create("Illegal:Idle")
                .ConditionalThen(ShouldToggleAi, _ => module.SetAi(false, $"準備前往「{GetName()}」（不在戰鬥中）"))
                .Then(_ => vnav.Stop())
                .Then(_ => state = ActivityState.Pathfinding);
        };
    }

    private Func<Chain> GetPathfindingChain(StateManagerModule states)
    {
        return () =>
        {
            var playerShard = AethernetData.AllByDistance().First();
            var activityShard = GetAethernetData();

            var isFate = data.Type == EventType.Fate;
            var source = isFate ? TravelSource.IllegalModeFate : TravelSource.IllegalModeCriticalEncounter;
            var navType = SmartNavigation.Decide(Player.Position, GetPosition(), activityShard);

            module.Debug("Selected navigation type: " + navType);
            Record.PathfindingRuns++;
            Record.PathfindingStartedAt = Environment.TickCount64;
            Record.Navigation = navType.ToString();

            var delayMs = TakeTravelDelay(isFate);
            returnHandedOver = false;
            var chain = Chain.Create("Illegal:Pathfinding")
                .ConditionalWait(_ => delayMs > 0, delayMs);

            switch (navType)
            {
                case NavigationType.Walk:
                    AddFinalLeg(chain, source)
                        .ConditionalThen(_ => ShouldMountToPathfindTo(GetPosition()), ChainHelper.MountChain());
                    break;

                case NavigationType.ReturnWalk:
                    var returnOnly = ChainHelper.ReturnChain();
                    AddFinalLeg(chain.Then(returnOnly).Then(_ => NoteReturnHandOver(returnOnly, source)), source)
                        .ConditionalThen(_ => ShouldMountToPathfindTo(GetPosition()), ChainHelper.MountChain());
                    break;

                case NavigationType.ReturnTeleportWalk:
                    var returnFirst = ChainHelper.ReturnChain(new ReturnChainConfig { ApproachAetheryte = true });
                    AddFinalLeg(chain
                            .Then(returnFirst)
                            .Then(_ => NoteReturnHandOver(returnFirst, source))
                            .ConditionalThen(_ => !returnHandedOver, ChainHelper.TeleportChain(activityShard.Aethernet))
                            .Debug("Waiting for lifestream to not be 'busy'")
                            .Then(new TaskManagerTask(() => returnHandedOver || !lifestream.IsBusy(), new TaskManagerConfiguration { TimeLimitMS = 30000 })), source)
                        .ConditionalThen(_ => ShouldMountToPathfindTo(GetPosition()), ChainHelper.MountChain());
                    break;

                case NavigationType.WalkTeleportWalk:
                    AddFinalLeg(chain
                            .Then(ChainHelper.PathfindToAndWait(playerShard.Position, AethernetData.DISTANCE, TravelSource.IllegalModeShard, null, () => module.Config.YieldToManualMovement))
                            .Then(ChainHelper.TeleportChain(activityShard.Aethernet))
                            .Debug("Waiting for lifestream to not be 'busy'")
                            .Then(new TaskManagerTask(() => !lifestream.IsBusy(), new TaskManagerConfiguration { TimeLimitMS = 30000 })), source)
                        .ConditionalThen(_ => ShouldMountToPathfindTo(GetPosition()), ChainHelper.MountChain());
                    break;
            }

            chain
                .Then(GetPathfindingWatcher(states))
                .Then(_ => state = GetPostPathfindingState());

            return chain;
        };
    }

    // 「前往緊急遭遇戰前先延遲」的截止時刻與起算來源（記錄用）；null＝這個活動還沒算過。只在框架執行緒讀寫。
    private long? travelDelayDeadline;

    private string travelDelayAnchor = string.Empty;

    /// <summary>
    /// 這一趟趕路鏈開頭要等的毫秒數（規則見 <see cref="CriticalEncounterDelayRules"/>）；F.A.T.E.、設定關著回 0。
    /// 只在框架執行緒呼叫（趕路鏈的工廠裡）。
    /// </summary>
    private int TakeTravelDelay(bool isFate)
    {
        if (isFate || !module.Config.ShouldDelayCriticalEncounters)
        {
            return 0;
        }

        var now = Environment.TickCount64;
        var first = travelDelayDeadline == null;
        if (first)
        {
            var openAgeMs = RegistrationOpenAgeMs(out var unknownReason);
            var anchor = CriticalEncounterDelayRules.Anchor(Record.SelectedAt, now, openAgeMs, out var fromOpen);
            travelDelayDeadline = CriticalEncounterDelayRules.Deadline(anchor,
                Random.Shared.Next(CriticalEncounterDelayRules.MinMs, CriticalEncounterDelayRules.MaxMs + 1));
            var opened = openAgeMs < 0 ? "?" : LedgerRules.Seconds(openAgeMs);
            travelDelayAnchor = fromOpen ? $"起算=開放報名 開放後={opened}" : $"起算=選定:{(openAgeMs < 0 ? unknownReason : "開放時間不合理")} 開放後={opened}";
        }

        var ms = CriticalEncounterDelayRules.RemainingMs(travelDelayDeadline!.Value, now);
        if (first || ms > 0)
        {
            Svc.Log.Information($"[BOCCHI][出發延遲] kind={KindLabel} id={data.Id} name=「{GetName()}」 等={LedgerRules.Seconds(ms)} 趟={Record.PathfindingRuns} "
                                + $"選定後={LedgerRules.Seconds(now - Record.SelectedAt)} 報名剩={RegistrationSecondsLeft()} {travelDelayAnchor} 設定=前往緊急遭遇戰前先延遲");
        }
        else
        {
            Svc.Log.Debug($"[BOCCHI][出發延遲] kind={KindLabel} id={data.Id} 趟={Record.PathfindingRuns} 已等過，直接出發");
        }

        return ms;
    }

    /// <summary>出發延遲記錄用：剩餘報名秒數；不是緊急遭遇戰或讀不到時回「?」。只在框架執行緒呼叫。</summary>
    protected virtual string RegistrationSecondsLeft()
    {
        return "?";
    }

    /// <summary>出發延遲的起算：這一輪報名開始到現在的毫秒數；不知道回 -1，並給記錄用的原因（不含空白）。只在框架執行緒呼叫。</summary>
    protected virtual long RegistrationOpenAgeMs(out string unknownReason)
    {
        unknownReason = "不是緊急遭遇戰";
        return -1;
    }

    private Func<Chain> GetParticipatingChain(StateManagerModule states)
    {
        return () =>
        {
            var fellBack = false;
            return Chain.Create("Illegal:Participating")
                .ConditionalThen(_ => module.Config.ShouldToggleAiProvider, _ => module.SetAi(true, $"開始參加「{GetName()}」"))
                .Then(_ => vnav.Stop())
                .Then(new TaskManagerTask(() =>
                {
                    if (FallBackFromEarlyEngage())
                    {
                        fellBack = true;
                        return true;
                    }

                    if (!module.Config.ShouldForceTarget || !EzThrottler.Throttle("Participating.ForceTarget", 500))
                    {
                        return states.GetState() == State.Idle;
                    }

                    var mode = module.Config.ShouldForceTargetCentralEnemy ? TargetPickMode.Centroid : TargetPickMode.Closest;
                    targets.Apply(GetEnemies(), mode, module.Config.TargetMinHoldSeconds, "參加中");

                    return states.GetState() == State.Idle;
                }, new TaskManagerConfiguration { TimeLimitMS = int.MaxValue }))
                .Then(_ => state = fellBack ? ActivityState.Pathfinding : ActivityState.Done);
        };
    }

    /// <summary>參加中每幀呼叫：提早接戰之後太久沒進入戰鬥就回 true，這一場改回趕路（照舊走近到接戰距離）。只在 framework 執行緒呼叫。</summary>
    protected virtual bool FallBackFromEarlyEngage()
    {
        return false;
    }

    private Func<Chain>? GetDoneChain(StateManagerModule states)
    {
        return null;
    }

    protected List<IBattleNpc> GetEnemies()
    {
        return TargetHelper.Enemies.Where(IsActivityTarget).ToList();
    }

    protected abstract bool IsActivityTarget(IBattleNpc obj);

    private AethernetData GetAethernetData()
    {
        return data.Aethernet?.GetData() ?? AethernetData.AllByDistance(GetPosition()).First();
    }

    protected bool IsInZone()
    {
        var radius = data.Radius ?? GetRadius();

        return Player.DistanceTo(GetPosition()) <= radius;
    }

    private bool ShouldMountToPathfindTo(Vector3 destination)
    {
        if (!module.PluginConfig.TeleporterConfig.ShouldMount || returnHandedOver)
        {
            return false;
        }

        // 戰鬥中叫不出坐騎：MountChain 會一路重試到逾時（10 秒、一行 Warning），這段期間尋路監看不會開始。
        if (Svc.Condition[ConditionFlag.InCombat])
        {
            return false;
        }

        return Vector3.Distance(Player.Position, destination) > 20f;
    }

    // 剛下的 PathfindAndMoveTo 要等背景算完路徑才會開始走，這段期間 Path.IsRunning 仍是 false。
    protected bool IsNavigating()
    {
        return vnav.IsRunning() || vnav.IsSimpleMoveInProgress();
    }

    // 只在框架執行緒用；每次停止前重新抓，不跨呼叫保存內容。
    private readonly OwnMoveDestinations travelDestinations = new();

    // 每拍都可能進來的停止點各一個（Foreign 那一行的節流）；不經建構子建出的物件第一次用時才建。
    private ForeignPathNotice? routeStopNotice;

    private ForeignPathNotice? yieldStopNotice;

    /// <summary>趕路到達時只停 BOCCHI 自己的路線（判法見 <see cref="StopOwnMove"/>）。</summary>
    protected void StopOwnTravel(string tag)
    {
        StopOwnMove(tag, "到達", null);
    }

    /// <summary>
    /// 只停 BOCCHI 為這個活動送的路線：自己的目的地＝最近送出的、還沒作廢的趕路登記，再加上這個活動的目的地
    /// （趕路登記被作廢時也認得出走向活動的路）。Own／Unknown 停；別人的路線不停並寫一行 Information，
    /// <paramref name="notice"/> 不是 null 時同一處 <see cref="ForeignPathNotice.IntervalMs"/> 內只寫一行。只在 framework 執行緒呼叫。
    /// </summary>
    internal OwnPathVerdict StopOwnMove(string tag, string when, ForeignPathNotice? notice)
    {
        travelDestinations.CaptureCurrent(TravelIntent.State);
        travelDestinations.Add(GetPosition());
        var verdict = OwnPathStop.StopIfOwn(vnav, travelDestinations, out var end);
        if (verdict == OwnPathVerdict.Foreign && (notice == null || notice.Due(Environment.TickCount64)))
        {
            Svc.Log.Information($"{tag} {when}「{GetName()}」時 vnavmesh 正在走別人的路線（終點離 BOCCHI 送過的目的地 {travelDestinations.NearestXZ(end!.Value):F1} 米）：不停，交給送出它的插件或你自己");
        }

        return verdict;
    }

    private bool combatPauseReported;

    /// <summary>
    /// 趕路途中被打（戰鬥中但不在 F.A.T.E.／緊急遭遇戰裡）而且移動已經停下時回 true：尋路監看原地等戰鬥結束，
    /// 不擲 <see cref="VnavmeshStoppedException"/>（那會讓整條動作鏈每秒中止重來）。只在 framework 執行緒呼叫。
    /// </summary>
    protected bool PauseForCombat(StateManagerModule states, bool stopped)
    {
        if (states.GetState() != State.InCombat)
        {
            combatPauseReported = false;
            return false;
        }

        if (!stopped)
        {
            return false;
        }

        if (!combatPauseReported)
        {
            combatPauseReported = true;
            Svc.Log.Information($"[BOCCHI][非法模式] 前往「{GetName()}」途中進入戰鬥、移動已停下：暫停尋路，戰鬥結束後再繼續（不會替你打怪，戰鬥 AI 維持你自己的設定）。");
        }

        return true;
    }

    private bool targetSummaryLogged;

    /// <summary>活動結束時呼叫（重複呼叫只寫第一次）：寫一行 [BOCCHI][目標] 摘要。只在 framework 執行緒呼叫。</summary>
    public virtual void LogTargetSummary(ActivityEnd end)
    {
        if (targetSummaryLogged)
        {
            return;
        }

        targetSummaryLogged = true;
        try
        {
            Svc.Log.Information(targets.Stats.Summary(targets.Source, GetName(), LedgerRules.Label(end), module.Config.TargetMinHoldSeconds));
        }
        catch (Exception ex)
        {
            // 與其他 Framework.Update 處理器共用 Dalamud 的 try/catch：擲出去會讓同一幀之後的處理器被跳過。
            Svc.Log.Warning($"[BOCCHI][目標摘要錯誤] {ex.GetType().Name}：{ex.Message}（不影響自動化）");
        }
    }

    /// <summary>趕路正在讓位給你（或你正在自己移動、導航剛停還沒站穩）：這時不送 BOCCHI 自己的移動。只在框架執行緒讀。</summary>
    public bool IsTravelYielding(long now)
    {
        return travelHolding && now - travelHoldingAt <= TravelHoldFreshMs;
    }

    /// <summary>活動中心（估算順路距離用）；只在框架執行緒讀。</summary>
    public Vector3 Destination
    {
        get => GetPosition();
    }

    /// <summary>活動範圍半徑，取法同 <see cref="IsInZone"/>。</summary>
    public float DestinationRadius
    {
        get => data.Radius ?? GetRadius();
    }

    /// <summary>活動目前的進度（0～100）；給活動帳在狀態轉換時讀，不要每幀呼叫。</summary>
    public abstract int ReadProgress();

    /// <summary>活動帳與記錄用的種類：FATE 或 CE。</summary>
    public abstract string KindLabel { get; }

    protected abstract float GetRadius();

    protected abstract TaskManagerTask GetPathfindingWatcher(StateManagerModule states);

    public abstract bool IsValid();

    protected abstract Vector3 GetPosition();

    public abstract string GetName();

    protected abstract ActivityState GetPostPathfindingState();
}
