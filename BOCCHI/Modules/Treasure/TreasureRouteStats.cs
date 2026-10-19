using System;
using System.Collections.Generic;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Pathfinding;

namespace BOCCHI.Modules.Treasure;

/// <summary>主視窗「路線」選單裡一段的數字；Remaining、秒數小於 0＝不知道（畫 ?）。</summary>
public readonly record struct TreasureRouteRow(int Count, int Remaining, float RoundSeconds, float RemainingSeconds)
{
    public static readonly TreasureRouteRow Unknown = new(-1, -1, -1f, -1f);
}

/// <summary>分段路線的統計。純邏輯、不碰遊戲；只在框架執行緒用。</summary>
public static class TreasureRouteStats
{
    /// <summary>成本換算成秒用的速度（米/秒）：實機完整一輪的成本表成本 ÷ 耗時，含開箱與上下坐騎。</summary>
    public const float MetersPerSecond = 17.5f;

    /// <summary><paramref name="nodes"/> 裡還沒巡過的（沒巡過、或巡過但有已記錄的寶箱），不含前往失敗的；順序不變。</summary>
    public static List<uint> RemainingOf(List<uint> nodes, TreasureHuntMemory memory)
    {
        return nodes.FindAll(n => !memory.IsCovered(n) && !memory.IsUnreachable(n));
    }

    /// <summary>從 <paramref name="route"/> 的乙太之晶出發走完 <paramref name="nodes"/> 的估計秒數（見 <see cref="BasePathfinder.EstimateRound"/>）；算不出來回 -1。</summary>
    public static float EstimateSeconds(BasePathfinder estimator, Aethernet route, List<uint> nodes)
    {
        var cost = estimator.EstimateRound(route, nodes);
        return float.IsFinite(cost) ? cost / MetersPerSecond : -1f;
    }

    /// <summary>
    /// 已記錄、巡迴開得到（對得上生成點、不是刻意不去的、沒有前往失敗、在區域等級上限內）而不在所選路線上的寶箱數；
    /// 對不上任何一段的點算在其他路線。
    /// </summary>
    public static int CountOtherRouteChests(TreasureHuntMemory memory, int maxLevel, Func<Aethernet, bool> selected)
    {
        var count = 0;
        var chests = memory.Chests;
        for (var i = 0; i < chests.Count; i++)
        {
            var node = chests[i].Node;
            if (node == 0 || TreasureData.IsAvoided(node) || memory.IsUnreachable(node)
                || !TreasureData.Levels.TryGetValue(node, out var level) || level > maxLevel)
            {
                continue;
            }

            if (!TreasureRoutes.Nodes.TryGetValue(node, out var route) || !selected(route))
            {
                count++;
            }
        }

        return count;
    }
}
