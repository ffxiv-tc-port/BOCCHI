using System.Numerics;
using BOCCHI.Data;

namespace BOCCHI.Pathfinding;

public enum TravelRouteVerdict : byte
{
    /// <summary>先走中繼點。</summary>
    Use,

    /// <summary>已經在中繼點之後（例：站在台上）：直接過去。</summary>
    AlreadyPast,

    /// <summary>比中繼點離目的地還近：直接過去。</summary>
    NearDestination,

    /// <summary>繞中繼點多走太多：直接過去。</summary>
    TooMuchDetour,

    /// <summary>這個活動已經試過一次路線、沒走到中繼點：這一趟直接過去。</summary>
    Retried,
}

/// <summary>趕路監看每一拍問路線的結果。</summary>
public enum RouteTick : byte
{
    /// <summary>沒有在走路線（含這一拍剛結束、已改送往活動）。</summary>
    None,

    /// <summary>還在用尋路走向中繼點或下一段的起跳點：停下重送照常，但不追敵人。</summary>
    Walking,

    /// <summary>正在跳某一段（停穩、直線往前、空中、剛落地）：這一拍不送任何移動、也不做停下重送。</summary>
    Hopping,
}

/// <summary>固定路線（<see cref="TravelRoute"/>）的判斷；只看傳進來的座標，純邏輯、不碰遊戲、不配置。</summary>
public static class TravelRouteRules
{
    /// <summary>「已經過了中繼點」的寬容：離目的地比中繼點最多遠這麼多米也算。</summary>
    public const float PastMarginM = 10f;

    /// <summary>走向中繼點的上限；超過就放棄路線、直接前往目的地。</summary>
    public const long ViaTimeLimitMs = 90_000;

    public static TravelRouteVerdict Decide(Vector3 player, Vector3 destination, TravelRoute route, bool triedBefore, out float detour)
    {
        var direct = LedgeClimb.Flat(player, destination);
        var viaToDestination = LedgeClimb.Flat(route.Via, destination);
        detour = LedgeClimb.Flat(player, route.Via) + viaToDestination - direct;
        if (triedBefore)
        {
            return TravelRouteVerdict.Retried;
        }

        if (IsPast(player, destination, route))
        {
            return TravelRouteVerdict.AlreadyPast;
        }

        if (direct <= viaToDestination)
        {
            return TravelRouteVerdict.NearDestination;
        }

        return detour > route.MaxDetourM ? TravelRouteVerdict.TooMuchDetour : TravelRouteVerdict.Use;
    }

    public static bool IsPast(Vector3 player, Vector3 destination, TravelRoute route)
    {
        return player.Y >= route.AboveY && LedgeClimb.Flat(player, destination) <= LedgeClimb.Flat(route.Via, destination) + PastMarginM;
    }

    /// <summary>走到中繼點了（或已經過了它）：改送往目的地。</summary>
    public static bool Reached(Vector3 player, Vector3 destination, TravelRoute route)
    {
        return LedgeClimb.Flat(player, route.Via) <= route.ViaReachM || IsPast(player, destination, route);
    }

    /// <summary>不跳的一段：離起點水平這麼近就開始直線走（騎乘約 0.2 秒的路，送出時還在動、不會停下）。</summary>
    public const float WalkThroughStartM = 3f;

    /// <summary>直線走了這麼久還沒過去就放棄路線。</summary>
    public const long WalkThroughLimitMs = 8_000;

    /// <summary>直線走了這麼久以上、導航卻停了（被擋住，或使用者按了移動鍵讓 vnavmesh 取消）：放棄路線。</summary>
    public const long WalkThroughStallMs = 1_000;

    /// <summary>不跳的一段（<see cref="RouteHop.Jump"/>＝false）這一拍該做什麼；<paramref name="startedAt"/>＝開始直線走的時刻，null＝還沒開始。</summary>
    public static WalkThroughAction WalkThrough(Vector3 player, RouteHop hop, long? startedAt, long now, bool navigating, bool holding)
    {
        if (hop.Arrived(player))
        {
            return WalkThroughAction.Arrived;
        }

        if (startedAt is not { } since)
        {
            if (holding)
            {
                return WalkThroughAction.Hold;
            }

            return LedgeClimb.Flat(player, hop.Approach) <= WalkThroughStartM ? WalkThroughAction.Start : WalkThroughAction.Approach;
        }

        var ms = now - since;
        if (holding || (ms > WalkThroughStallMs && !navigating))
        {
            return WalkThroughAction.Stalled;
        }

        return ms > WalkThroughLimitMs ? WalkThroughAction.GiveUp : WalkThroughAction.Moving;
    }
}

public enum WalkThroughAction : byte
{
    /// <summary>已經在落點那一側：這一段過了。</summary>
    Arrived,

    /// <summary>還沒到起點：尋路走過去。</summary>
    Approach,

    /// <summary>還沒開始直線走、使用者在手動移動：不送移動，只登記起點（讓位結束後走向它）。</summary>
    Hold,

    /// <summary>到了起點附近：直線走向落點（不停下）。</summary>
    Start,

    /// <summary>直線走著、還沒過去。</summary>
    Moving,

    /// <summary>直線走著卻停了，或使用者在手動移動：放棄路線但不送移動，交給趕路監看照原本的路接續（停下重送／讓位）。</summary>
    Stalled,

    /// <summary>直線走太久還沒過去（導航還在走）：停下、放棄路線、直接前往目的地。</summary>
    GiveUp,
}
