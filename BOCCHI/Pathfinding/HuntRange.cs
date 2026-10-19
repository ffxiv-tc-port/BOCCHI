using BOCCHI.Modules.Pathfinder;

namespace BOCCHI.Pathfinding;

/// <summary>
/// 一輪巡迴的範圍：用的區域等級上限與來源、上限以內的點數（含已巡過／已跑過的）、資料裡會去的點數
/// （等級 ≤ <see cref="PathfinderConfig.LevelCeiling"/>，不含刻意不去的點）。Total 為 0＝還沒規劃。
/// </summary>
public readonly record struct HuntRange(int MaxLevel, MaxLevelSource Source, int InRange, int Total);
