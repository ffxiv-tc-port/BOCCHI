using System.Collections.Generic;
using BOCCHI.Enums;

namespace BOCCHI.Pathfinding;

public enum StartWalkSource
{
    /// <summary>vnavmesh 從角色目前位置算出來的路長。</summary>
    Measured,

    /// <summary>量不到（失敗、逾時、沒量），改用直線距離：比實際短，所以偏向步行。</summary>
    Straight,

    /// <summary>vnavmesh 說終點走不到（路徑停在離終點太遠的地方）。</summary>
    Unreachable,
}

/// <summary>從角色目前位置走到某個點的路長；Straight＝直線距離（任何來源都一併記，給同分時比較）。</summary>
public readonly record struct StartWalk(float Meters, StartWalkSource Source, float Straight)
{
    public bool Reachable
    {
        get => Source != StartWalkSource.Unreachable;
    }
}

/// <summary>
/// 規劃第一段用的輸入：候選點（已記錄的寶箱）與附近乙太之晶各自的 <see cref="StartWalk"/>。
/// 只在 framework 執行緒建，交給規劃之後不再改。
/// </summary>
public sealed class StartLegCosts
{
    public Dictionary<uint, StartWalk> Nodes { get; } = [];

    public Dictionary<Aethernet, StartWalk> Shards { get; } = [];
}

public enum StartLegKind
{
    Walk,

    /// <summary>走到 From 的乙太之晶、傳送到 To。</summary>
    ShardTeleport,

    /// <summary>返回營地，To 不是營地時再傳送到 To。</summary>
    ReturnTeleport,
}

/// <param name="TourCost">從 Node 出發、照成本表排完全部已記錄寶箱的成本。</param>
/// <param name="Walk">直接步行到 Node。</param>
/// <param name="ShardWalk">走到 From 的乙太之晶；只有 ShardTeleport 有意義。</param>
public sealed record StartLegChoice(uint Node, StartLegKind Kind, float LegCost, float TourCost, Aethernet From, Aethernet To, StartWalk Walk, StartWalk ShardWalk, List<PathfinderStep> Steps);
