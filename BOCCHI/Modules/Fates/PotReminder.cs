using System;
using System.Collections.Generic;

namespace BOCCHI.Modules.Fates;

/// <summary>
/// 魔法甕「快出現」的判斷，以及「快出現」「出現了」兩種提醒的去重。純計算、不碰遊戲，只在 framework 執行緒用；時間一律 UTC。
/// 「不知道」＝這一座島還沒看過魔法甕出現，或推估落空過（<see cref="PotCycle.TryPredictReliable" /> 回 false）：一律不提醒、不跳過。
/// </summary>
public sealed class PotReminder
{
    /// <summary>同一個魔法甕這段時間內不再提醒「出現了」：F.A.T.E. 清單短暫少掉再回來時不再響一次。</summary>
    public static readonly TimeSpan SpawnRenotifyFloor = TimeSpan.FromMinutes(10);

    // 兩次推估的出現時間差在這之內算同一次出現：錨點從「看到的時間」換成遊戲記錄的開始時間只差幾秒。
    private static readonly TimeSpan SameSpawnTolerance = TimeSpan.FromMinutes(2);

    private readonly Dictionary<uint, DateTime> spawnAlertedAt = new();

    // default＝這一座島還沒提醒過「快出現」。
    private DateTime soonAlertedFor;

    /// <summary>
    /// 推估下一個魔法甕在 <paramref name="minutes" /> 分鐘內出現（推估時間已過、還在寬限內也算，這時 <paramref name="remaining" /> 是負的）。
    /// <paramref name="minutes" /> ≤ 0 或不知道時回 false。
    /// </summary>
    public static bool IsDueWithin(PotCycle cycle, DateTime nowUtc, TimeSpan period, int minutes, out uint nextId, out TimeSpan remaining)
    {
        remaining = default;
        if (minutes <= 0 || !cycle.TryPredictReliable(nowUtc, period, out nextId, out var nextUtc))
        {
            nextId = 0;
            return false;
        }

        remaining = nextUtc - nowUtc;
        return remaining <= TimeSpan.FromMinutes(minutes);
    }

    /// <summary>這一幀要不要提醒「快出現」：推估時間前 <paramref name="minutes" /> 分鐘內、還沒到推估時間；同一次推估只回一次 true。</summary>
    public bool TakeSoon(PotCycle cycle, DateTime nowUtc, TimeSpan period, int minutes, out uint nextId, out TimeSpan remaining)
    {
        if (!IsDueWithin(cycle, nowUtc, period, minutes, out nextId, out remaining) || remaining <= TimeSpan.Zero)
        {
            return false;
        }

        var nextUtc = nowUtc + remaining;
        if (soonAlertedFor != default && (nextUtc - soonAlertedFor).Duration() <= SameSpawnTolerance)
        {
            return false;
        }

        soonAlertedFor = nextUtc;
        return true;
    }

    /// <summary>某個魔法甕新出現（或進島後第一次看到）：同一個魔法甕 <see cref="SpawnRenotifyFloor" /> 內只回一次 true。</summary>
    public bool TakeSpawn(uint fateId, DateTime nowUtc)
    {
        if (spawnAlertedAt.TryGetValue(fateId, out var last) && nowUtc - last < SpawnRenotifyFloor)
        {
            return false;
        }

        spawnAlertedAt[fateId] = nowUtc;
        return true;
    }

    /// <summary>換了一座島（或登出）：之前的提醒記錄一律作廢。</summary>
    public void Reset()
    {
        spawnAlertedAt.Clear();
        soonAlertedFor = default;
    }
}
