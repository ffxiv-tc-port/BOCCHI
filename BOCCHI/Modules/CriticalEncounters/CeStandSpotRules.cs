using System;
using System.Collections.Generic;
using System.Numerics;
using BOCCHI.Pathfinding;

namespace BOCCHI.Modules.CriticalEncounters;

// 本檔契約：純邏輯，只看傳進來的值（離線驗收直接呼叫）；距離一律只看 X／Z，高度另外判。
// 報名範圍＝地圖標記的中心＋DynamicEvent 表 Unknown4（新月島每一場都是 20）；實機報名範圍不比它小，所以只往內縮、不往外放。

/// <summary>挑報名站位點的結果。</summary>
public enum CeSpotOutcome : byte
{
    /// <summary>找到空位：呼叫端走過去。</summary>
    Picked,

    /// <summary>到了之後附近沒人疊著（只有 <see cref="CeStandSpotRules.Nudge"/> 會回）：不動。</summary>
    Stayed,

    /// <summary>試滿 <see cref="CeStandSpotRules.MaxTries"/> 次都找不到：照原本的目的地。</summary>
    NoRoom,

    /// <summary>報名半徑讀不到或太小：照原本的目的地。</summary>
    NoArea,

    /// <summary>挑點時擲例外（多半是 vnavmesh 沒就緒）：照原本的目的地。</summary>
    Failed,
}

/// <param name="FromCenter">選到的點離報名中心的水平距離。</param>
/// <param name="NearestOther">選到的點（Stayed＝角色）離最近其他玩家的水平距離；沒有其他玩家時是正無限大。</param>
/// <param name="Tries">產生了幾個候選點。</param>
/// <param name="FloorQueries">其中送去投影到地面的次數（先過了範圍與間距才查）；FloorMisses＝查不到或查到的點不合格。</param>
public readonly record struct CeSpotResult(CeSpotOutcome Outcome, Vector3 Point, float FromCenter, float NearestOther, int Tries, int FloorQueries, int FloorMisses);

public static class CeStandSpotRules
{
    /// <summary>離報名範圍邊緣至少留這麼多：投影到地面最多偏 0.7 米、vnavmesh 停在目的地前約 1 米，再多留 1 米。</summary>
    public const float EdgeMarginM = 3f;

    /// <summary>離其他玩家至少這麼遠：玩家碰撞半徑 0.5，兩人 1 米就貼在一起，體型大的種族肩寬約 1 米。</summary>
    public const float ClearanceM = 2f;

    /// <summary>地面點與參考高度最多差這麼多（往上找地面也從參考高度加這麼多開始往下找）。</summary>
    public const float HeightWindowM = 4f;

    /// <summary>
    /// 找中心的地面（參考高度）時從地圖標記往上抬這麼多再往下找：標記常比地面低零點幾米，
    /// 「防衛指令」的標記更在甲板下 5.3 米、一塊走不到的網格上。
    /// </summary>
    public const float ReferenceLiftM = DestinationFloor.LiftM;

    /// <summary>投影到地面後水平最多偏這麼多，超過就當作那裡沒有地面。</summary>
    public const float MaxFloorShiftM = 1f;

    /// <summary>到了之後的微調：從 <see cref="ClearanceM"/> 往外最多再走這麼遠。</summary>
    public const float NudgeRingM = 3f;

    public const int MaxTries = 24;

    /// <summary>離報名中心這麼近（水平）才挑點：物件表看得到範圍裡的玩家。</summary>
    public const float ApproachM = 50f;

    /// <summary>停下時離自己挑的點這麼近才算走到（才做微調）。</summary>
    public const float ArrivedM = 2f;

    /// <summary>列舉其他玩家時，離報名中心多遠以內才要（範圍＋間距＋微調都蓋到）。</summary>
    public static float OthersReach(float radius)
    {
        return radius + ClearanceM + NudgeRingM;
    }

    /// <summary>可以站的半徑（報名半徑往內縮 <see cref="EdgeMarginM"/>）。</summary>
    public static float StandRadius(float radius)
    {
        return radius - EdgeMarginM;
    }

    public static float DistanceXZ(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>離最近其他玩家的水平距離；沒有其他玩家時回正無限大。</summary>
    public static float NearestXZ(Vector3 point, IReadOnlyList<Vector3> others)
    {
        var best = float.PositiveInfinity;
        for (var i = 0; i < others.Count; i++)
        {
            best = MathF.Min(best, DistanceXZ(point, others[i]));
        }

        return best;
    }

    /// <summary>
    /// 報名範圍內（往內縮邊距）隨機一點：面積上均勻、離其他玩家至少 <see cref="ClearanceM"/>、投影得到地面、
    /// 高度與中心的地面（<see cref="ReferenceLiftM"/>）差不超過 <see cref="HeightWindowM"/>。
    /// </summary>
    /// <param name="floor">把點投影到地面（找 Y 不高於傳入點的最高地面）；查不到回 null。</param>
    public static CeSpotResult Pick(Vector3 center, float radius, IReadOnlyList<Vector3> others, Random random, Func<Vector3, Vector3?> floor)
    {
        var stand = StandRadius(radius);
        if (!(stand >= ClearanceM) || !float.IsFinite(radius) || !float.IsFinite(center.X) || !float.IsFinite(center.Z))
        {
            return new CeSpotResult(CeSpotOutcome.NoArea, center, 0f, float.PositiveInfinity, 0, 0, 0);
        }

        var referenceY = DestinationFloor.Project(floor, center).Y;
        return Search(center, stand, referenceY, center, 0f, stand, others, random, floor);
    }

    /// <summary>
    /// 走到之後呼叫：最近的其他玩家不到 <see cref="ClearanceM"/> 才在角色周圍 <see cref="ClearanceM"/>～<see cref="ClearanceM"/>＋<see cref="NudgeRingM"/>
    /// 找一點（同樣要在可站範圍內、離其他玩家夠遠、高度與角色差不超過 <see cref="HeightWindowM"/>）；不疊著回 Stayed。
    /// </summary>
    public static CeSpotResult Nudge(Vector3 center, float radius, Vector3 player, IReadOnlyList<Vector3> others, Random random, Func<Vector3, Vector3?> floor)
    {
        var stand = StandRadius(radius);
        if (!(stand >= ClearanceM) || !float.IsFinite(radius))
        {
            return new CeSpotResult(CeSpotOutcome.NoArea, player, DistanceXZ(player, center), NearestXZ(player, others), 0, 0, 0);
        }

        var nearest = NearestXZ(player, others);
        if (nearest >= ClearanceM)
        {
            return new CeSpotResult(CeSpotOutcome.Stayed, player, DistanceXZ(player, center), nearest, 0, 0, 0);
        }

        return Search(center, stand, player.Y, player, ClearanceM, ClearanceM + NudgeRingM, others, random, floor);
    }

    private static CeSpotResult Search(Vector3 center, float stand, float referenceY, Vector3 origin, float innerRadius, float outerRadius,
        IReadOnlyList<Vector3> others, Random random, Func<Vector3, Vector3?> floor)
    {
        var inner2 = innerRadius * innerRadius;
        var outer2 = outerRadius * outerRadius;
        var queries = 0;
        var misses = 0;
        for (var tries = 1; tries <= MaxTries; tries++)
        {
            // 半徑取平方根：圓（環）面積上均勻，不會擠在中心。
            var r = MathF.Sqrt(inner2 + (float)random.NextDouble() * (outer2 - inner2));
            var angle = (float)(random.NextDouble() * Math.Tau);
            var candidate = new Vector3(origin.X + MathF.Cos(angle) * r, referenceY + HeightWindowM, origin.Z + MathF.Sin(angle) * r);
            if (DistanceXZ(candidate, center) > stand || NearestXZ(candidate, others) < ClearanceM)
            {
                continue;
            }

            queries++;
            if (floor(candidate) is not { } ground
                || DistanceXZ(ground, candidate) > MaxFloorShiftM
                || MathF.Abs(ground.Y - referenceY) > HeightWindowM
                || DistanceXZ(ground, center) > stand)
            {
                misses++;
                continue;
            }

            var nearest = NearestXZ(ground, others);
            if (nearest < ClearanceM)
            {
                misses++;
                continue;
            }

            return new CeSpotResult(CeSpotOutcome.Picked, ground, DistanceXZ(ground, center), nearest, tries, queries, misses);
        }

        return new CeSpotResult(CeSpotOutcome.NoRoom, origin, DistanceXZ(origin, center), NearestXZ(origin, others), MaxTries, queries, misses);
    }
}
