namespace BOCCHI.Modules.Treasure;

public enum TreasureSightSkip : byte
{
    /// <summary>照常：換成輔助自由人施放魔尋寶再換回。</summary>
    None,

    /// <summary>讀不到輔助自由人的等級：當作還沒學會。</summary>
    LevelUnknown,

    /// <summary>輔助自由人還沒到 <see cref="TreasureSightRules.LearnLevel"/> 級、還沒學會魔尋寶。</summary>
    NotLearned,

    /// <summary>距上次確認施放還沒超過使用者設的重新探測間隔。</summary>
    Interval,
}

/// <summary>返回時要不要換成輔助自由人施放魔尋寶；只看傳進來的值，純邏輯、不碰遊戲。</summary>
public static class TreasureSightRules
{
    /// <summary>輔助自由人學會魔尋寶的等級（MKDSupportJob 第 0 列：Action[1]＝41651 魔尋寶、LevelUnlock[1]＝10）。</summary>
    public const int LearnLevel = 10;

    /// <summary>同一個略過原因，這麼久內只寫一次 Information，其餘寫 Debug。</summary>
    public const long ReportEveryMs = 10 * 60_000;

    /// <param name="freelancerLevel">輔助自由人目前的等級；-1＝讀不到。</param>
    /// <param name="lastCastAt">上一次確認放出去的時刻（毫秒）；null＝這次載入插件後還沒放過。</param>
    /// <param name="intervalMinutes">重新探測間隔；0＝每次返回都放。</param>
    /// <param name="remainingMs">略過原因是 <see cref="TreasureSightSkip.Interval"/> 時還剩幾毫秒；其餘 0。</param>
    public static TreasureSightSkip Decide(int freelancerLevel, long? lastCastAt, long now, int intervalMinutes, out long remainingMs)
    {
        remainingMs = 0;
        if (freelancerLevel < 0)
        {
            return TreasureSightSkip.LevelUnknown;
        }

        if (freelancerLevel < LearnLevel)
        {
            return TreasureSightSkip.NotLearned;
        }

        if (intervalMinutes > 0 && lastCastAt is { } at && at + (intervalMinutes * 60_000L) - now is > 0 and var left)
        {
            remainingMs = left;
            return TreasureSightSkip.Interval;
        }

        return TreasureSightSkip.None;
    }

    public static bool ShouldReport(TreasureSightSkip reason, TreasureSightSkip lastReason, long lastReportAt, long now)
    {
        return reason != lastReason || now - lastReportAt >= ReportEveryMs;
    }
}
