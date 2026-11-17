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
        StopOwnMove("[BOCCHI][路線]", "路線停下、前往", routeStopNotice ??= new ForeignPathNotice());
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
}
