namespace BOCCHI.Modules.Automator;

/// <summary>非法模式前往 F.A.T.E. 途中，要不要改去正在報名的緊急遭遇戰；只看傳進來的數字（離線驗收直接編譯這個檔）。</summary>
public static class CriticalEncounterTravelRules
{
    /// <param name="thresholdSeconds">剩餘報名秒數不超過它才改去；≤ 0＝只要正在報名就改去。</param>
    /// <param name="startTimestamp">緊急遭遇戰開始（報名截止）的 Unix 秒；≤ 0＝讀不到，照上游當成要改去。</param>
    /// <param name="remainingSeconds">剩餘報名秒數（可能是負的）；讀不到時是 -1。</param>
    public static bool ShouldLeaveFateTravel(int thresholdSeconds, long startTimestamp, long nowUnixSeconds, out long remainingSeconds)
    {
        if (startTimestamp <= 0)
        {
            remainingSeconds = -1;
            return true;
        }

        remainingSeconds = startTimestamp - nowUnixSeconds;
        return thresholdSeconds <= 0 || remainingSeconds <= thresholdSeconds;
    }
}
