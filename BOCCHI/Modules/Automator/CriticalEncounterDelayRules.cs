using System;

namespace BOCCHI.Modules.Automator;

/// <summary>
/// 設定「前往緊急遭遇戰前先延遲」：這一場開放報名後的 10～15 秒內不出發。每個活動只抽一次、只算一個截止時刻；
/// 趕路鏈中止重來時只等剩下的部分，等過了就直接出發。只看傳進來的數字（離線驗收直接呼叫）。
/// </summary>
public static class CriticalEncounterDelayRules
{
    public const int MinMs = 10000;

    public const int MaxMs = 15000;

    // 開放報名到現在超過這麼久還在報名＝這筆記錄多半不是這一輪（報名期實測 180～187 秒），改從選定算。
    public const long MaxOpenAgeMs = 240_000;

    /// <summary>
    /// 起算時刻：插件看到這一輪報名開始的時刻（不晚於選定、不超過 <see cref="MaxOpenAgeMs"/>）；不知道就從選定活動算。
    /// 看到的時刻不早於真正開放、選定也在開放之後，所以兩種都不會比「開放後 10～15 秒」更早出發。
    /// </summary>
    /// <param name="selectedAt">選定活動的時刻（<see cref="Environment.TickCount64"/>）。</param>
    /// <param name="openAgeMs">開放報名到 <paramref name="now"/> 的毫秒數；&lt; 0＝不知道。</param>
    public static long Anchor(long selectedAt, long now, long openAgeMs, out bool fromOpen)
    {
        var opened = now - openAgeMs;
        fromOpen = openAgeMs >= 0 && openAgeMs <= MaxOpenAgeMs && opened <= selectedAt;
        return fromOpen ? opened : selectedAt;
    }

    /// <param name="anchor">起算時刻（<see cref="Anchor"/>）。</param>
    /// <param name="rolledMs">抽到的延遲；夾在 0～<see cref="MaxMs"/>。</param>
    public static long Deadline(long anchor, int rolledMs)
    {
        return anchor + Math.Clamp(rolledMs, 0, MaxMs);
    }

    /// <returns>這一趟還要等的毫秒數；0＝不用等。</returns>
    public static int RemainingMs(long deadline, long now)
    {
        var left = deadline - now;
        return left <= 0 ? 0 : (int)Math.Min(left, MaxMs);
    }
}
