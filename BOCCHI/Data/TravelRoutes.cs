using System.Collections.Generic;
using System.Numerics;
using BOCCHI.Enums;
using BOCCHI.Pathfinding;

namespace BOCCHI.Data;

/// <summary>
/// 一條固定路線：先走到 <see cref="Via"/>（水平 <see cref="ViaReachM"/> 米內算到了），再照常前往目的地。
/// 高度至少 <see cref="AboveY"/>、而且比中繼點離目的地近（多 <see cref="Pathfinding.TravelRouteRules.PastMarginM"/> 米內）＝已經過了，不繞；
/// 先繞中繼點比直接過去多走（水平直線）超過 <see cref="MaxDetourM"/> 也不繞。
/// </summary>
/// <param name="Label">記錄用名稱。</param>
/// <param name="Hops">網格接不起來的幾段（依序）；有的話中繼點＝第一段的起點，過了最後一段才前往目的地。null＝沒有。</param>
public sealed record TravelRoute(string Label, Vector3 Via, float ViaReachM, float AboveY, float MaxDetourM, RouteHop[]? Hops = null);

/// <summary>
/// 路線上網格接不起來的一段：<see cref="Jump"/>＝要跳（尋路走到 <see cref="Approach"/>、停穩後直線往 <see cref="Landing"/> 走並跳，<see cref="LedgeClimb"/>）；
/// 否則走得過去、只是 vnavmesh 不會規劃（例：往下的落差）：走近 <see cref="Approach"/> 就不停下、直線走向 <see cref="Landing"/>。
/// <see cref="Arrived"/> 才算過了：離落點水平 <see cref="ArriveRadiusM"/> 米內，而且 <see cref="Below"/>＝往下時高度不超過
/// <see cref="ArriveY"/>、往上時不低於。
/// </summary>
public sealed record RouteHop(string Label, Vector3 Approach, Vector3 Landing, float ArriveY, bool Below, float ArriveRadiusM, bool Jump)
{
    public HuntLedge Ledge
    {
        get => new(Approach, Landing, ArriveY, ArriveRadiusM);
    }

    public bool Arrived(Vector3 player)
    {
        return (Below ? player.Y <= ArriveY : player.Y >= ArriveY) && LedgeClimb.Flat(player, Landing) <= ArriveRadiusM;
    }
}

/// <summary>vnavmesh 自己算的路不好走、使用者指定要改走的路線；座標取自使用者實機自己走的那一趟（BMR 重播的角色位置）。</summary>
public static class TravelRoutes
{
    /// <summary>F.A.T.E. id → 路線；沒列的 F.A.T.E. 照 vnavmesh 自己算的路。</summary>
    public static readonly Dictionary<uint, TravelRoute> Fates = new()
    {
        // 土壤守護者——癲泥怪：從石塔水沼過來時走北邊樓梯（底 (-536, 73, 310) → 頂 (-592, 97, 284)），中繼點取樓梯中段。
        { 1969, new TravelRoute("北邊樓梯", new Vector3(-563.81f, 85.95f, 288.48f), 6f, 94f, 60f) },

        // 甲板清掃者——巨大鳥：從放浪神聖域遺跡過來時走北線（Z 約 -590～-605），只過一段水。網格在下水、上岸、下溝三處沒接起來
        // （vnavmesh 自己只會繞去北邊兩段水路）；只有上岸非跳不可，下水、下溝是往下的落差、直線走下去就好；溝西邊的坡網格接得上、照常尋路。
        // 起點與落點都是使用者實機走的位置。過了沒的半徑只涵蓋落點那一側：判錯成沒過去會從水裡／溝裡尋路繞回起點（80～355 米）。
        {
            1965, new TravelRoute("北線", new Vector3(-427.7f, 3.8f, -595.1f), LedgeClimb.ApproachReachM, float.MaxValue, 40f,
            [
                new RouteHop("下水", new Vector3(-427.7f, 3.8f, -595.1f), new Vector3(-444.8f, -0.3f, -592.8f), 1.0f, true, 15f, false),
                new RouteHop("上岸", new Vector3(-459.8f, 2.7f, -590.5f), new Vector3(-471.1f, 3.1f, -591.6f), 2.5f, false, 8.5f, true),
                new RouteHop("下溝", new Vector3(-489.4f, 3.1f, -598.5f), new Vector3(-498.8f, 1.0f, -602.1f), 2.9f, true, 10f, false),
            ])
        },
    };

    /// <summary>乙太之晶在台上、自動尋路常卡在台階下：起跳點＝實機卡住的位置，落點＝使用者跳上去後站到的位置。</summary>
    public static readonly Dictionary<Aethernet, HuntLedge[]> ShardStairs = new()
    {
        {
            Aethernet.TheWanderersHaven, [
                new HuntLedge(new Vector3(-158.1f, 6.1f, -614.9f), new Vector3(-165.5f, 7.3f, -614.5f), ShardStairsTopY, ShardStairsTopRadius),
                new HuntLedge(new Vector3(-172.4f, 6.0f, -602.6f), new Vector3(-170.4f, 7.3f, -610.6f), ShardStairsTopY, ShardStairsTopRadius),
            ]
        },
    };

    // 台上（含晶體旁 Y 6.5 的平台、台階頂 Y 7.3）與台階下（Y 6.0～6.2）的分界；離晶體水平 12 米內才算台上。
    private const float ShardStairsTopY = 6.3f;

    private const float ShardStairsTopRadius = 12f;

    /// <summary>離晶體水平這麼近、又在台階下時才跳；更遠照常讓 vnavmesh 走。</summary>
    public const float ShardStairsActivateM = 25f;

    /// <summary>
    /// 走向 <paramref name="aethernet"/> 的晶體、角色在台階下而且夠近：挑起跳點離角色最近的那一組。
    /// 沒有這座的資料、已經在台上、太遠或比台面高（從高處下來不用跳）都回 false。
    /// </summary>
    public static bool TryPickShardStairs(Aethernet aethernet, Vector3 shard, Vector3 player, out HuntLedge ledge)
    {
        ledge = default;
        if (!ShardStairs.TryGetValue(aethernet, out var ledges) || player.Y >= ShardStairsTopY || LedgeClimb.Flat(player, shard) > ShardStairsActivateM)
        {
            return false;
        }

        var best = float.MaxValue;
        foreach (var candidate in ledges)
        {
            var distance = LedgeClimb.Flat(player, candidate.Approach);
            if (distance < best)
            {
                best = distance;
                ledge = candidate;
            }
        }

        return best < float.MaxValue;
    }
}
