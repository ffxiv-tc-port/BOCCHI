using System;
using System.Numerics;
using Ocelot.IPC;

namespace BOCCHI.Pathfinding;

/// <summary>
/// 前往活動的目的地投影到地面：先往上抬 <see cref="LiftM"/> 再找「不高於這一點的最高地面」，查不到用原值。
/// 地圖標記常比地面低零點幾米，直接投影會落到地面下走不到的孤島；「防衛指令」的標記更在甲板下 5.3 米，抬的高度要蓋過它。
/// 刻意不用 NearestPoint：它會選到同一個孤島。vnavmesh IPC 只在框架執行緒呼叫。
/// </summary>
public static class DestinationFloor
{
    public const float LiftM = 8f;

    /// <summary>投影時水平方向的搜尋半徑（米）。</summary>
    public const float HalfExtentXZ = 0.5f;

    public static Vector3 Project(VNavmesh vnav, Vector3 point)
    {
        return vnav.FindPointOnFloor(point with { Y = point.Y + LiftM }, false, HalfExtentXZ) ?? point;
    }

    /// <param name="floor">找「不高於傳入點的最高地面」；查不到回 null（純邏輯呼叫端與離線驗收用）。</param>
    public static Vector3 Project(Func<Vector3, Vector3?> floor, Vector3 point)
    {
        return floor(point with { Y = point.Y + LiftM }) ?? point;
    }
}
