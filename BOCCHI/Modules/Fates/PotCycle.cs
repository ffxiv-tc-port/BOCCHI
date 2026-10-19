using System;

namespace BOCCHI.Modules.Fates;

/// <summary>
/// 魔法甕 F.A.T.E. 出現時間的推算。純計算、不碰遊戲，只在 framework 執行緒用；時間一律 UTC。
/// 假設兩個魔法甕以固定週期交替出現（週期由呼叫端給）。這只是推估：每次親眼看到出現都重新定錨，
/// 並記下與上一次的實測間隔，供使用者校準週期。
/// </summary>
public sealed class PotCycle
{
    /// <summary>幸福的魔法甕。</summary>
    public const uint PersistentPots = 1976;

    /// <summary>瑟瑟發抖的魔法甕。</summary>
    public const uint PleadingPots = 1977;

    /// <summary>推估時間過了這麼久仍沒看到出現，才改推下一輪。</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    // 兩個魔法甕各自記「在不在表上」：萬一兩場重疊，也不會在兩者之間每幀來回判成新出現。
    private bool persistentActive;

    private bool pleadingActive;

    /// <summary>目前在 F.A.T.E. 表上的魔法甕（兩場都在時取最後出現的那場）；0＝沒有。</summary>
    public uint ActiveId
    {
        get
        {
            if (AnchorId != 0 && IsActive(AnchorId))
            {
                return AnchorId;
            }

            return persistentActive ? PersistentPots : pleadingActive ? PleadingPots : 0;
        }
    }

    /// <summary>最後一次親眼看到出現的魔法甕；0＝還沒看過，無法推估。</summary>
    public uint AnchorId { get; private set; }

    public DateTime AnchorSpawnUtc { get; private set; }

    /// <summary>false＝遊戲沒給開始時間，<see cref="AnchorSpawnUtc" /> 是第一次看到的時間（可能偏晚）。</summary>
    public bool AnchorFromGameTime { get; private set; }

    /// <summary>上一次與這一次親眼看到出現之間的間隔；null＝只看過一次。</summary>
    public TimeSpan? LastInterval { get; private set; }

    public static bool IsPot(uint fateId)
    {
        return fateId is PersistentPots or PleadingPots;
    }

    public static uint Opposite(uint fateId)
    {
        return fateId switch
        {
            PersistentPots => PleadingPots,
            PleadingPots => PersistentPots,
            _ => 0,
        };
    }

    public bool IsActive(uint fateId)
    {
        return fateId switch
        {
            PersistentPots => persistentActive,
            PleadingPots => pleadingActive,
            _ => false,
        };
    }

    public void Reset()
    {
        persistentActive = false;
        pleadingActive = false;
        AnchorId = 0;
        AnchorSpawnUtc = default;
        AnchorFromGameTime = false;
        LastInterval = null;
    }

    /// <summary>某個魔法甕這一幀在 F.A.T.E. 表上。</summary>
    public PotObservation Observe(uint fateId, DateTime spawnUtc, bool fromGameTime)
    {
        if (!IsPot(fateId))
        {
            return PotObservation.Ignored;
        }

        if (IsActive(fateId))
        {
            // 同一場：只在遊戲晚一步給出開始時間時把錨點換成較準的那個。
            if (fromGameTime && !AnchorFromGameTime && AnchorId == fateId)
            {
                AnchorSpawnUtc = spawnUtc;
                AnchorFromGameTime = true;
                return PotObservation.Refined;
            }

            return PotObservation.Same;
        }

        LastInterval = AnchorId != 0 ? spawnUtc - AnchorSpawnUtc : null;
        SetActive(fateId, true);
        AnchorId = fateId;
        AnchorSpawnUtc = spawnUtc;
        AnchorFromGameTime = fromGameTime;
        return PotObservation.NewSpawn;
    }

    /// <summary>魔法甕已經不在 F.A.T.E. 表上（結束或消失）。</summary>
    public bool Ended(uint fateId)
    {
        if (!IsActive(fateId))
        {
            return false;
        }

        SetActive(fateId, false);
        return true;
    }

    private void SetActive(uint fateId, bool active)
    {
        if (fateId == PersistentPots)
        {
            persistentActive = active;
        }
        else if (fateId == PleadingPots)
        {
            pleadingActive = active;
        }
    }

    /// <summary>下一個魔法甕的推估；還沒看過任何魔法甕時回 false。</summary>
    /// <param name="missed">推估時間過了 <see cref="Grace" /> 仍沒看到、因此往後推了幾輪。</param>
    public bool TryPredict(DateTime nowUtc, TimeSpan period, out uint nextId, out DateTime nextUtc, out int missed)
    {
        nextId = 0;
        nextUtc = default;
        missed = 0;
        if (AnchorId == 0 || period < TimeSpan.FromMinutes(1))
        {
            return false;
        }

        // 最小的 k≥1 使 now ≤ 錨點 + k·週期 + 寬限。
        var overdue = (nowUtc - AnchorSpawnUtc - Grace).Ticks;
        var k = overdue > period.Ticks ? (overdue + period.Ticks - 1) / period.Ticks : 1;

        nextId = k % 2 == 1 ? Opposite(AnchorId) : AnchorId;
        nextUtc = AnchorSpawnUtc + TimeSpan.FromTicks(period.Ticks * k);
        missed = (int)Math.Min(k - 1, int.MaxValue);
        return true;
    }

    /// <summary>
    /// 同 <see cref="TryPredict" />，但推估落空過（往後推了輪數）也回 false：那時週期已經對不上，不拿來做決定。
    /// 回 true 時 <paramref name="nextUtc" /> 可能已經過了（還在 <see cref="Grace" /> 內、還沒看到出現）。
    /// </summary>
    public bool TryPredictReliable(DateTime nowUtc, TimeSpan period, out uint nextId, out DateTime nextUtc)
    {
        if (TryPredict(nowUtc, period, out nextId, out nextUtc, out var missed) && missed == 0)
        {
            return true;
        }

        nextId = 0;
        nextUtc = default;
        return false;
    }
}

public enum PotObservation
{
    Ignored,

    /// <summary>已經在追蹤的同一場。</summary>
    Same,

    /// <summary>同一場，開始時間改用遊戲給的值。</summary>
    Refined,

    /// <summary>新出現（或進島後第一次看到）。</summary>
    NewSpawn,
}
