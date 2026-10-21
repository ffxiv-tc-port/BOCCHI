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

public abstract class Activity
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

    /// <summary>這個活動的固定路線（<see cref="TravelRoutes"/>）；null＝照 vnavmesh 自己算的路（與沒有這個功能時相同）。</summary>
    protected virtual TravelRoute? Route
    {
        get => null;
    }

    // 正在走向固定路線的中繼點；null＝沒有在走路線。只在框架執行緒讀寫。
    private TravelRoute? routing;

    private long routingSince;

    // 這個活動試過一次路線（重來時不再繞）。
    private bool routeTried;

    // 走到第幾段（TravelRoute.Hops 的索引）、最後送給 vnavmesh 的走向點（同一點不重送）、跳躍狀態機（第一次用時才建）、
    // 不跳的那一段開始直線走的時刻（null＝還沒開始）。
    private int routeHop;

    private Vector3? routeSentTo;

    private LedgeClimb? routeClimb;

    private long? routeStraightSince;

    // 返回讓給使用者操作了（ReturnChain 放棄施放）：這一趟不傳送、不送導航、不叫坐騎，趕路監看一開始就在讓位。只在框架執行緒讀寫。
    private bool returnHandedOver;

    private void NoteReturnHandOver(ReturnChain chain, TravelSource source)
    {
        returnHandedOver = chain.Yielded;
        if (!returnHandedOver)
        {
            return;
        }

        // 同路線「直線走到一半停了」的做法：不送移動、只登記活動本身，接續或重送時走向這一點。
        TravelIntent.Register(DestinationFloor.Project(vnav, GetPosition()), source);
        Svc.Log.Information(module.Config.YieldToManualMovement
            ? $"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} name=「{GetName()}」 返回交給你操作：這一趟先不傳送、不送導航、不叫坐騎；"
              + $"你停手（角色靜止 {ManualMoveDetector.ResumeQuietMs / 1000f:F1} 秒）後從目前位置接續前往，超過 {ManualMoveDetector.MaxYieldMs / 1000} 秒照舊停下重送 {TravelStateFields()}"
            : $"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} name=「{GetName()}」 返回交給你操作：這一趟不傳送、不送導航、不叫坐騎；"
              + $"「讓位給手動移動」關著，照停下重送的規則接續 {TravelStateFields()}");
    }

    /// <summary>趕路監看每一拍最先呼叫：返回剛交給使用者操作時，這一趟從讓位開始（選項關著時照停下重送）。只在框架執行緒呼叫。</summary>
    protected void TakeReturnHandOver(TravelStopGuard guard)
    {
        if (!returnHandedOver)
        {
            return;
        }

        returnHandedOver = false;
        guard.YieldEnabled = module.Config.YieldToManualMovement;
        guard.BeginHandOver(Environment.TickCount64);
    }

    // 最後走過去的那一段。有固定路線時到了這一步（傳送完、角色站穩）才決定要不要先走中繼點。
    private Chain AddFinalLeg(Chain chain, TravelSource source)
    {
        return Route == null
            ? chain.ConditionalThen(_ => !returnHandedOver, new PathfindingChain(vnav, GetPosition(), data, source))
            : chain.ConditionalThen(_ => !returnHandedOver, () => BeginRouteLeg(source));
    }

    private Chain BeginRouteLeg(TravelSource source)
    {
        var route = Route!;
        var player = Player.Position;
        var verdict = TravelRouteRules.Decide(player, GetPosition(), route, routeTried, out var detour);
        if (verdict != TravelRouteVerdict.Use)
        {
            routing = null;
            var why = verdict switch
            {
                TravelRouteVerdict.AlreadyPast => "已經過了",
                TravelRouteVerdict.NearDestination => "比中繼點離目的地還近",
                TravelRouteVerdict.TooMuchDetour => $"繞過去多走 {detour:F0} 米（上限 {route.MaxDetourM:F0}）",
                _ => "這個活動已經試過一次、沒走到",
            };
            Svc.Log.Information($"[BOCCHI][路線] kind={KindLabel} id={data.Id} name=「{GetName()}」 不經{route.Label}（{why}）：照原本的路直接過去；角色位置 {player:F1}");
            return new PathfindingChain(vnav, GetPosition(), data, source).Factory()();
        }

        routeTried = true;
        routing = route;
        routingSince = Environment.TickCount64;
        routeHop = 0;
        routeSentTo = route.Via;
        routeClimb?.Reset();
        routeStraightSince = null;
        var hops = route.Hops is { Length: > 0 } list
            ? $"，接著依序過 {list.Length} 段（{string.Join("、", list.Select(h => h.Jump ? h.Label + "〔跳〕" : h.Label))}）"
            : string.Empty;
        Svc.Log.Information($"[BOCCHI][路線] kind={KindLabel} id={data.Id} name=「{GetName()}」 經{route.Label}：先走到 {route.Via:F1}（多走約 {detour:F0} 米）{hops}，到了再前往活動；角色位置 {player:F1}");
        return new PathfindAndMoveToChain(vnav, route.Via, source).Factory()();
    }

    /// <summary>
    /// 趕路監看每一拍先呼叫：走到中繼點（或已經過了它）、跳完最後一段就改送往活動；跳不過去或走太久就放棄路線、直接前往。
    /// 只在框架執行緒呼叫。
    /// </summary>
    /// <param name="holding">趕路正在讓位給手動移動：還沒起跳就先不跳、不送走向起跳點的移動。</param>
    protected RouteTick TickRoute(TravelSource source, bool holding)
    {
        var route = routing;
        if (route == null)
        {
            return RouteTick.None;
        }

        var player = Player.Position;
        var elapsed = Environment.TickCount64 - routingSince;
        if (route.Hops is not { Length: > 0 } hops)
        {
            var reached = TravelRouteRules.Reached(player, GetPosition(), route);
            if (!reached && elapsed <= TravelRouteRules.ViaTimeLimitMs)
            {
                return RouteTick.Walking;
            }

            EndRoute(source, reached
                ? $"經過{route.Label}（{elapsed / 1000f:F1} 秒）：接著前往活動；角色位置 {player:F1}"
                : $"{TravelRouteRules.ViaTimeLimitMs / 1000} 秒內沒走到{route.Label}：放棄路線，直接前往活動；角色位置 {player:F1}");
            return RouteTick.None;
        }

        if (elapsed > TravelRouteRules.ViaTimeLimitMs)
        {
            StopRouteMove();
            EndRoute(source, $"{TravelRouteRules.ViaTimeLimitMs / 1000} 秒內沒走完{route.Label}（停在第 {routeHop + 1} 段「{hops[routeHop].Label}」）：放棄路線，直接前往活動；角色位置 {player:F1}");
            return RouteTick.None;
        }

        return TickHop(route, hops, player, elapsed, holding, source);
    }

    // 路線上網格接不起來的那幾段（過了沒＝RouteHop.Arrived）：要跳的照 LedgeClimb，不跳的直線走過去；過完最後一段或放棄就改送往活動。
    private RouteTick TickHop(TravelRoute route, RouteHop[] hops, Vector3 player, long elapsed, bool holding, TravelSource source)
    {
        var hop = hops[routeHop];
        var next = routeHop + 1 < hops.Length ? hops[routeHop + 1] : null;
        var now = Environment.TickCount64;
        if (!hop.Jump)
        {
            return TickWalkThrough(route, hops, hop, next, player, elapsed, now, holding, source);
        }

        var climb = routeClimb ??= new LedgeClimb();
        var action = climb.Step(now, routeHop, player, hop.Arrived(player), Svc.Condition[ConditionFlag.Jumping], holding, hop.Ledge);
        if (action == LedgeAction.Jump)
        {
            var sent = Hunter.TryJump();
            Svc.Log.Debug($"{RouteWhere(route, hop)}第 {climb.Attempts + 1} 次：{(sent ? "跳" : "跳躍送不出去")}；角色位置 {player:F2}");
            action = climb.JumpSent(sent, now);
        }

        switch (action)
        {
            case LedgeAction.Approach:
                // 讓位中只先登記（讓位結束接續時才走向這一段的起跳點），不送移動。
                if (holding)
                {
                    if (TravelIntent.State.Current.Destination != hop.Approach)
                    {
                        TravelIntent.Register(hop.Approach, source);
                    }
                }
                else if (routeSentTo != hop.Approach)
                {
                    routeSentTo = hop.Approach;
                    TravelIntent.Register(hop.Approach, source);
                    vnav.PathfindAndMoveTo(hop.Approach, false);
                }

                return RouteTick.Walking;
            case LedgeAction.Stop:
                StopRouteMove();
                return RouteTick.Hopping;
            case LedgeAction.RunUp:
                // 下一段的起點接在落點後面：落地判定那幾拍不停下；之後停下重送時也走向它，不回頭繞回這一段的起跳點。
                routeSentTo = next?.Approach;
                if (next != null)
                {
                    TravelIntent.Register(next.Approach, source);
                }

                OwnMoveDestinations.Note(hop.Landing);
                vnav.MoveTo(next == null ? [hop.Landing] : [hop.Landing, next.Approach], false);
                return RouteTick.Hopping;
            case LedgeAction.Retry:
                StopRouteMove();
                Svc.Log.Information($"{RouteWhere(route, hop)}第 {climb.Attempts} 次沒過去（{climb.LastFailure}），回起跳點 {hop.Approach:F1} 重來；角色位置 {player:F2}");
                return RouteTick.Hopping;
            case LedgeAction.Landed:
            case LedgeAction.Proceed:
                Svc.Log.Information(action == LedgeAction.Landed
                    ? $"{RouteWhere(route, hop)}第 {climb.Attempts + 1} 次跳過去了；角色位置 {player:F2}"
                    : $"{RouteWhere(route, hop)}還沒跳就已經在落點那一側；角色位置 {player:F2}");
                return NextHop(route, hops, player, elapsed, source);
            case LedgeAction.GiveUp:
                StopRouteMove();
                EndRoute(source, $"{route.Label}第 {routeHop + 1} 段「{hop.Label}」跳了 {climb.Attempts} 次都沒過去（最後一次：{climb.LastFailure}）：放棄路線，直接前往活動；角色位置 {player:F2}");
                return RouteTick.None;
            default:
                return RouteTick.Hopping;
        }
    }

    // 不跳的一段（規則見 TravelRouteRules.WalkThrough）：走近起點就直線走向落點、不停下；下一段要跳就把它的起跳點接在後面。
    private RouteTick TickWalkThrough(TravelRoute route, RouteHop[] hops, RouteHop hop, RouteHop? next, Vector3 player, long elapsed, long now, bool holding,
        TravelSource source)
    {
        switch (TravelRouteRules.WalkThrough(player, hop, routeStraightSince, now, IsNavigating(), holding))
        {
            case WalkThroughAction.Arrived:
                Svc.Log.Information($"{RouteWhere(route, hop)}直線走過去了（沒跳）；角色位置 {player:F2}");
                routeStraightSince = null;
                return NextHop(route, hops, player, elapsed, source);
            case WalkThroughAction.Approach:
                if (routeSentTo != hop.Approach)
                {
                    routeSentTo = hop.Approach;
                    TravelIntent.Register(hop.Approach, source);
                    vnav.PathfindAndMoveTo(hop.Approach, false);
                }

                return RouteTick.Walking;
            case WalkThroughAction.Hold:
                if (TravelIntent.State.Current.Destination != hop.Approach)
                {
                    TravelIntent.Register(hop.Approach, source);
                }

                routeSentTo = null;
                return RouteTick.Walking;
            case WalkThroughAction.Start:
                routeStraightSince = now;
                Svc.Log.Debug($"{RouteWhere(route, hop)}直線走向 {hop.Landing:F1}（不跳）；角色位置 {player:F2}");
                OwnMoveDestinations.Note(hop.Landing);
                if (next is { Jump: true })
                {
                    // 之後停下重送（停在下一段起跳點前）時走向它，不回頭繞回這一段的起點。
                    TravelIntent.Register(next.Approach, source);
                    routeSentTo = next.Approach;
                    vnav.MoveTo([hop.Landing, next.Approach], false);
                }
                else
                {
                    routeSentTo = hop.Landing;
                    vnav.MoveTo([hop.Landing], false);
                }

                return RouteTick.Hopping;
            case WalkThroughAction.GiveUp:
                StopRouteMove();
                EndRoute(source, $"{route.Label}第 {routeHop + 1} 段「{hop.Label}」直線走了 {(now - routeStraightSince) / 1000f:F1} 秒還沒過去：放棄路線，直接前往活動；角色位置 {player:F2}");
                return RouteTick.None;
            case WalkThroughAction.Stalled:
                // 不送移動：登記活動本身，交給趕路監看照原本的路接續（停下重送，或使用者手動移動時讓位）。
                routing = null;
                routeStraightSince = null;
                TravelIntent.Register(DestinationFloor.Project(vnav, GetPosition()), source);
                Svc.Log.Information($"{RouteWhere(route, hop)}直線走到一半停了（{(holding ? "手動移動" : "導航停了")}）：放棄路線，照原本的路前往活動；角色位置 {player:F2}");
                return RouteTick.None;
            default:
                return RouteTick.Hopping;
        }
    }

    // 記錄的開頭；只在要寫記錄時才組（跳躍與直線走的那幾拍每拍都會進來）。
    private string RouteWhere(TravelRoute route, RouteHop hop)
    {
        return $"[BOCCHI][路線] kind={KindLabel} id={data.Id} name=「{GetName()}」 {route.Label}第 {routeHop + 1} 段「{hop.Label}」";
    }

    // 這一段過了：換下一段（不停下，前一段送的移動照走），沒有下一段就改送往活動。
    private RouteTick NextHop(TravelRoute route, RouteHop[] hops, Vector3 player, long elapsed, TravelSource source)
    {
        routeHop++;
        if (routeHop < hops.Length)
        {
            return RouteTick.Hopping;
        }

        EndRoute(source, $"走完{route.Label}（{elapsed / 1000f:F1} 秒）：接著前往活動；角色位置 {player:F1}");
        return RouteTick.None;
    }

    private void StopRouteMove()
    {
        routeSentTo = null;
        OwnPathfind.CancelIfOnlyOwn(vnav, OwnMoveDestinations.HasCurrent(TravelIntent.State));
        vnav.Stop();
    }

    // 結束路線、改送往活動：與原本最後一段（PathfindingChain）相同，抬高後投影到地面上再送。
    private void EndRoute(TravelSource source, string what)
    {
        routing = null;
        Svc.Log.Information($"[BOCCHI][路線] kind={KindLabel} id={data.Id} name=「{GetName()}」 {what}");
        var destination = DestinationFloor.Project(vnav, GetPosition());
        TravelIntent.Register(destination, source);
        vnav.PathfindAndMoveTo(destination, false);
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

    /// <summary>
    /// 尋路監看就要擲例外（整條趕路鏈中止重來）之前呼叫：每個活動只寫第一次，後面幾次由活動帳的「重來」計數。
    /// 只讀狀態旗標與 IPC、不改任何判斷；只在 framework 執行緒呼叫。
    /// </summary>
    protected void ReportTravelInterrupt(string reason)
    {
        if (Record.TravelInterruptReported)
        {
            return;
        }

        Record.TravelInterruptReported = true;
        Svc.Log.Information($"[BOCCHI][趕路中斷] kind={KindLabel} id={data.Id} name=「{GetName()}」 原因={reason} {TravelStateFields()} 第={Record.PathfindingRuns}");
    }

    private string TravelStateFields()
    {
        var condition = Svc.Condition;
        var left = Vector3.Distance(Player.Position, GetPosition());
        var since = Record.PathfindingStartedAt == 0 ? "?" : ((Environment.TickCount64 - Record.PathfindingStartedAt) / 1000f).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        return $"離目的地={left:F1} 騎乘={YesNo(condition[ConditionFlag.Mounted])} "
               + $"上下坐騎中={YesNo(condition[ConditionFlag.MountOrOrnamentTransition])} 詠唱={YesNo(condition[ConditionFlag.Casting])} 戰鬥={YesNo(condition[ConditionFlag.InCombat])} "
               + $"vnav走={QueryVnav(() => vnav.IsRunning())} vnav算路={QueryVnav(() => vnav.IsSimpleMoveInProgress())} 網格算路={QueryVnav(() => vnav.IsPathfinding())} 這趟={since}";
    }

    /// <summary>上下坐騎、詠唱、跳躍、讀圖中：導航停著是暫時的，趕路監看不當成停下。</summary>
    internal static bool IsMovementPaused()
    {
        var condition = Svc.Condition;
        return condition[ConditionFlag.Mounting] || condition[ConditionFlag.Mounting71] || condition[ConditionFlag.MountOrOrnamentTransition]
               || condition[ConditionFlag.Casting] || condition[ConditionFlag.Casting87]
               || condition[ConditionFlag.Jumping] || condition[ConditionFlag.Jumping61]
               || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51];
    }

    /// <summary>
    /// 趕路監看每一拍呼叫（在動時也要呼叫，停下計時才會重來）。停下時照 <see cref="TravelStopGuard"/> 等、或在原地重送同一個目的地；
    /// 回 false＝重送用完，呼叫端照舊中止整趟重來。只在 framework 執行緒呼叫。
    /// </summary>
    protected bool KeepTravelling(TravelStopGuard guard, bool navigating, TravelSource source)
    {
        var paused = !navigating && IsMovementPaused();
        var decision = guard.Next(Vector3.Distance(Player.Position, GetPosition()), navigating, paused, Environment.TickCount64);
        if (decision == TravelDecision.Rewalk)
        {
            Record.TravelRewalks++;
            ReportOnce(ref Record.TravelRewalkReported, $"[BOCCHI][趕路重走] kind={KindLabel} id={data.Id} name=「{GetName()}」 重走第={guard.Rewalks} {TravelStateFields()}");
            SendTravel(source);
        }
        else if (decision == TravelDecision.Resume)
        {
            ReportOnce(ref Record.TravelResumeReported,
                $"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} name=「{GetName()}」 角色已靜止 {ManualMoveDetector.ResumeQuietMs / 1000f:F1} 秒：從目前位置接續前往（這個活動累計讓位 {Record.TravelYieldMs / 1000f:F1} 秒） {TravelStateFields()}");
            SendTravel(source);
        }

        return decision != TravelDecision.GiveUp;
    }

    /// <summary>
    /// 趕路監看每一拍、在每一條路徑上都呼叫（含原地等、追敵人、戰鬥暫停）：讓位取樣約每 100 毫秒一次，選項關著時什麼都不讀。
    /// 只在 framework 執行緒呼叫。
    /// </summary>
    protected void ObserveTravel(TravelStopGuard guard, bool navigating)
    {
        guard.YieldEnabled = module.Config.YieldToManualMovement;
        if (!guard.YieldEnabled)
        {
            travelHolding = false;
            guard.Manual.Reset();
            return;
        }

        var now = Environment.TickCount64;
        travelHolding = guard.HoldsOwnMoves(navigating);
        travelHoldingAt = now;
        // 不經建構子建出的物件第一次用時才建（同 Record）。按鍵每拍都讀：短按可能落在兩次取樣之間。
        handsOn ??= new HandsOnProbe();
        var input = handsOn.ReadInput(now);
        if (guard.Manual.External && navigating && vnav.IsRunning())
        {
            vnav.Stop();
            Svc.Log.Debug($"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} 為 Lifestream 讓位中 vnavmesh 開始走（BOCCHI 先前送的路算完了）：停下");
        }

        var player = Svc.Objects.LocalPlayer;
        if (!guard.ShouldSample(now) || player == null)
        {
            return;
        }

        lifestreamBusy ??= new LifestreamBusyProbe(lifestream);
        var interacting = !input && HandsOnProbe.Interacting(now);
        var step = guard.Observe(player.Position, navigating, !navigating && IsMovementPaused(), lifestreamBusy.Query(now), input || interacting, now);
        Record.TravelYieldMs += step.YieldedMs;
        if (guard.Yielding)
        {
            HandsOnRecent.Note(now, "趕路正在讓位給你操作");
        }

        switch (step.Event)
        {
            case TravelYieldEvent.Touched when guard.Manual.Capped:
                ReportOnce(ref Record.TravelCappedTouchReported,
                    $"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} name=「{GetName()}」 讓位滿 {ManualMoveDetector.MaxYieldMs / 1000} 秒後偵測到你還在操作（{(input ? "按了移動鍵" : "在互動：開寶箱、對話一類")}）："
                    + $"不重送，改成按鍵／互動讓位；沒有按鍵、沒在互動、角色靜止 {ManualMoveDetector.ResumeQuietMs / 1000f:F1} 秒後從目前位置接續；"
                    + $"這種讓位一趟累計上限 {ManualMoveDetector.TouchMaxMs / 1000} 秒 {TravelStateFields()}");
                break;
            case TravelYieldEvent.Touched:
                ReportOnce(ref Record.TravelTouchReported,
                    $"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} name=「{GetName()}」 導航停下時偵測到你在操作（{(input ? "按了移動鍵" : "在互動：開寶箱、對話一類")}）："
                    + $"不重送、不算重走次數；沒有按鍵、沒在互動、角色靜止 {ManualMoveDetector.ResumeQuietMs / 1000f:F1} 秒後從目前位置接續；"
                    + $"這種讓位一趟累計上限 {ManualMoveDetector.TouchMaxMs / 1000} 秒 {TravelStateFields()}");
                break;
            case TravelYieldEvent.TouchLimit:
                Svc.Log.Information($"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} name=「{GetName()}」 為按鍵／互動讓位累計超過 {ManualMoveDetector.TouchMaxMs / 1000} 秒："
                                    + $"這一趟之後只看持續移動，停下就照原本的重送／中止重來 {TravelStateFields()}");
                break;
            case TravelYieldEvent.External:
                var stopped = navigating && StopNavigationForLifestream();
                ReportOnce(ref Record.TravelExternalReported,
                    $"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} name=「{GetName()}」 Lifestream 正在執行任務（多半是你在地圖上點了目的地）："
                    + $"{(stopped ? "停下 BOCCHI 自己的導航、" : string.Empty)}讓位到它做完，這段時間不算進 {ManualMoveDetector.MaxYieldMs / 1000} 秒上限；"
                    + $"它做完、角色靜止 {ManualMoveDetector.ResumeQuietMs / 1000f:F1} 秒後從目前位置接續 {TravelStateFields()}");
                break;
            case TravelYieldEvent.ExternalLimit:
                Svc.Log.Information($"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} name=「{GetName()}」 為 Lifestream 讓位累計超過 {ManualMoveDetector.ExternalMaxMs / 1000} 秒："
                                    + $"這一趟之後不再為它讓位（照手動移動的規則） {TravelStateFields()}");
                break;
            case TravelYieldEvent.Started:
                ReportOnce(ref Record.TravelYieldReported,
                    $"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} name=「{GetName()}」 偵測到手動移動（導航停著、角色以 {step.Speed:F1} 米/秒持續移動 {ManualMoveDetector.ConfirmMs / 1000f:F1} 秒）："
                    + $"暫停自動趕路，角色靜止 {ManualMoveDetector.ResumeQuietMs / 1000f:F1} 秒後從目前位置接續；超過 {ManualMoveDetector.MaxYieldMs / 1000} 秒照舊中止重來 {TravelStateFields()}");
                break;
            case TravelYieldEvent.Expired:
                Svc.Log.Information($"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} name=「{GetName()}」 手動移動超過 {ManualMoveDetector.MaxYieldMs / 1000} 秒：這一趟改回原本的停下重送／中止重來 {TravelStateFields()}");
                break;
            case TravelYieldEvent.Cancelled:
                Svc.Log.Debug($"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} 讓位中導航又開始走：結束讓位、不另外接續");
                break;
        }
    }

    /// <summary>
    /// 趕路監看的任務設定：180 秒時限只算沒在讓位的時間（逾時那一刻把這段期間讓位的時間補回去）。
    /// 讓位本身另有上限（手動一次 30 秒、為 Lifestream 一趟 180 秒）：Lifestream 一直忙時最長約 360 秒，
    /// 它還一直帶角色走就再加用完後看起來像手動移動的 30 秒（約 390 秒）。
    /// </summary>
    protected TaskManagerConfiguration TravelWatcherConfig(TravelStopGuard guard)
    {
        return new TaskManagerConfiguration
        {
            TimeLimitMS = TravelWatcherLimitMs,
            ShowError = false,
            OnTaskTimeout = (TaskManagerTask _, ref long remaining) => ExtendForYield(guard, ref remaining),
        };
    }

    private const int TravelWatcherLimitMs = 180_000;

    // 任務自帶逾時處理器時 Ocelot 不印逾時警告：補回後仍不夠（真的會中止）由這裡印，等級沿用 Ocelot 的 Warning。
    private void ExtendForYield(TravelStopGuard guard, ref long remaining)
    {
        var credit = guard.TakeYieldCredit();
        remaining += credit;
        if (remaining < 0)
        {
            Svc.Log.Warning($"[BOCCHI][趕路中斷] kind={KindLabel} id={data.Id} name=「{GetName()}」 原因=趕路監看逾時（{TravelWatcherLimitMs / 1000} 秒，"
                            + $"已補回讓位 {credit / 1000f:F1} 秒）：中止這一趟、重新規劃 {TravelStateFields()} 第={Record.PathfindingRuns}");
            return;
        }

        if (credit <= 0)
        {
            return;
        }

        Svc.Log.Information($"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} name=「{GetName()}」 趕路監看到了 {TravelWatcherLimitMs / 1000} 秒時限，"
                            + $"其中讓位 {credit / 1000f:F1} 秒不算：時限往後延 {credit / 1000f:F1} 秒 {TravelStateFields()}");
    }

    // 同 Hunter：讓位時絕不取消算路（全域的，會連 Lifestream 在算的一起取消），只停已經在走的路；
    // BOCCHI 還在算的路算完開走時，讓位期間每拍再停（ObserveTravel）。回傳有沒有停下東西。
    private bool StopNavigationForLifestream()
    {
        var stopped = false;
        if (vnav.IsRunning())
        {
            vnav.Stop();
            stopped = true;
        }

        return stopped;
    }

    // 同一個活動第一次寫 Information，之後寫 Debug。
    private static void ReportOnce(ref bool reported, string line)
    {
        if (reported)
        {
            Svc.Log.Debug(line);
            return;
        }

        reported = true;
        Svc.Log.Information(line);
    }

    // 重送 PathfindingChain 登記的那個目的地（同一趟，迴避的登記不重開）；登記已作廢或不是這個來源才用活動中心。
    private void SendTravel(TravelSource source)
    {
        var intent = TravelIntent.State;
        var destination = intent.HasIntent && intent.Current.Source == source ? intent.Current.Destination : GetPosition();
        TravelIntent.Register(destination, source);
        vnav.PathfindAndMoveTo(destination, false);
    }

    private static string QueryVnav(Func<bool> query)
    {
        try
        {
            return YesNo(query());
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static string YesNo(bool value)
    {
        return value ? "是" : "否";
    }

    protected abstract float GetRadius();

    protected abstract TaskManagerTask GetPathfindingWatcher(StateManagerModule states);

    public abstract bool IsValid();

    protected abstract Vector3 GetPosition();

    public abstract string GetName();

    protected abstract ActivityState GetPostPathfindingState();
}
