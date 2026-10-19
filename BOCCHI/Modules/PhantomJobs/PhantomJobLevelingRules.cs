using System;

namespace BOCCHI.Modules.PhantomJobs;

/// <summary>練等模式「該不該換、換去哪」的判斷；只看傳進來的數字，不碰遊戲狀態（離線驗收直接編譯這個檔）。</summary>
/// <remarks>滿級 0＝表上沒有滿級（輔助自由人，不靠戰鬥經驗值升級）；等級 0＝還沒取得。</remarks>
public static class PhantomJobLevelingRules
{
    public static bool IsLevelable(byte maxLevel)
    {
        return maxLevel > 0;
    }

    /// <summary>目前這個輔助職業已滿級，或根本不靠經驗值升級時回 true。</summary>
    public static bool ShouldSwitchFrom(byte level, byte maxLevel)
    {
        return !IsLevelable(maxLevel) || level >= maxLevel;
    }

    /// <summary>目前的輔助職業該換時，依職業編號往後（繞回開頭）找第一個已取得、靠經驗值升級、還沒滿級的；不必換或找不到回 -1。</summary>
    public static int FindNext(ReadOnlySpan<byte> levels, ReadOnlySpan<byte> maxLevels, int current)
    {
        var count = Math.Min(levels.Length, maxLevels.Length);
        if (current < 0 || current >= count || !ShouldSwitchFrom(levels[current], maxLevels[current]))
        {
            return -1;
        }

        for (var offset = 1; offset < count; offset++)
        {
            var candidate = (current + offset) % count;
            var level = levels[candidate];
            if (IsLevelable(maxLevels[candidate]) && level > 0 && level < maxLevels[candidate])
            {
                return candidate;
            }
        }

        return -1;
    }
}
