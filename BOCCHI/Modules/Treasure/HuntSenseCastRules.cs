namespace BOCCHI.Modules.Treasure;

/// <summary>寶箱巡迴開始／續跑前要不要先補放魔尋寶；None 以外都是不補放的原因。</summary>
public enum HuntSenseCastSkip : byte
{
    None,

    /// <summary>「開滿感知數就跳過」或「開始前補放」沒開。</summary>
    Off,

    /// <summary>已經有能用的感知數。</summary>
    Known,

    /// <summary>正在參與 F.A.T.E.／緊急遭遇戰：這時收到的數字不採用。</summary>
    Participating,

    /// <summary>讀不到目前的輔助職業：換了之後換不回來。</summary>
    JobUnknown,

    LevelUnknown,

    NotLearned,

    InCombat,

    Casting,

    /// <summary>騎乘中，下坐騎的上限內沒下來。</summary>
    Mounted,

    /// <summary>換成輔助自由人後魔尋寶還在冷卻，等到上限還沒轉完。</summary>
    Cooldown,

    /// <summary>送出了但遊戲沒有開始冷卻（沒放出去）。</summary>
    NotCast,

    /// <summary>上限內沒換成輔助自由人：不送施放（同一個動作格在別的輔助職業是別的技能）。</summary>
    NotSwitched,

    /// <summary>魔尋寶的剩餘冷卻讀不到、或比冷卻全長加餘裕還長：不等。</summary>
    CooldownUnknown,
}

/// <summary>已換成輔助自由人、還沒送出施放時，魔尋寶的冷卻要不要等。</summary>
public enum HuntSenseCooldown : byte
{
    /// <summary>沒在冷卻：可以送。</summary>
    Ready,

    Cooling,

    GiveUp,
}

public enum HuntSenseCastStep : byte
{
    Waiting,

    /// <summary>遊戲開始冷卻＝放出去了。</summary>
    Sent,

    Cooldown,

    NotCast,

    NotSwitched,
}

/// <summary>開始前補放魔尋寶的判斷；只看傳進來的值，純邏輯、不碰遊戲。時間一律是呼叫端給的毫秒數。</summary>
public static class HuntSenseCastRules
{
    /// <summary>放出去之後最多等這麼久收魔尋寶的結果（實機約 0.3 秒內），之後照常開始、不跳過。</summary>
    public const long SenseWaitMs = 10_000;

    /// <summary>換成輔助自由人、冷卻轉完後最多這麼久要放出去；等冷卻的時間不算在內。</summary>
    public const long CastGiveUpMs = 3_000;

    /// <summary>魔尋寶的冷卻全長（Action 41651 的 Recast100ms＝100）。</summary>
    public const long RecastFullMs = 10_000;

    /// <summary>補放遇到冷卻最多等這麼久；讀到的剩餘冷卻比它長＝不合理，不等。</summary>
    public const long CooldownWaitMaxMs = RecastFullMs + 2_000;

    /// <summary>施放那一步的時限：等換職、等冷卻、送出重試各自的上限加餘裕。</summary>
    public const int CastStageLimitMs = (int)((2 * CastGiveUpMs) + CooldownWaitMaxMs + 5_000);

    /// <summary>送出後冷卻還沒開始時，隔這麼久再送一次（換職的動作硬直會讓第一次落空）。</summary>
    public const long CastRetryMs = 500;

    /// <summary>補放送出後巡迴最多等這麼久就照常規劃（動作鏈被別人收掉、收尾沒跑到時的後備上限）。</summary>
    public const long HoldLimitMs = 30_000;

    /// <summary>只看設定與記錄的前三項（不必讀遊戲）；None＝還要往下看 <see cref="Decide"/>。</summary>
    /// <param name="enabled">「開滿感知數就跳過」與「開始前補放」都開著。</param>
    public static HuntSenseCastSkip Precheck(bool enabled, bool known, bool participating)
    {
        if (!enabled)
        {
            return HuntSenseCastSkip.Off;
        }

        if (known)
        {
            return HuntSenseCastSkip.Known;
        }

        return participating ? HuntSenseCastSkip.Participating : HuntSenseCastSkip.None;
    }

    /// <param name="freelancerLevel">輔助自由人等級；-1＝讀不到。</param>
    public static HuntSenseCastSkip Decide(bool enabled, bool known, bool participating, bool jobKnown, int freelancerLevel, bool inCombat, bool casting)
    {
        var pre = Precheck(enabled, known, participating);
        if (pre != HuntSenseCastSkip.None)
        {
            return pre;
        }

        if (!jobKnown)
        {
            return HuntSenseCastSkip.JobUnknown;
        }

        var level = TreasureSightRules.Decide(freelancerLevel, null, 0, 0, out _);
        if (level == TreasureSightSkip.LevelUnknown)
        {
            return HuntSenseCastSkip.LevelUnknown;
        }

        if (level == TreasureSightSkip.NotLearned)
        {
            return HuntSenseCastSkip.NotLearned;
        }

        if (inCombat)
        {
            return HuntSenseCastSkip.InCombat;
        }

        return casting ? HuntSenseCastSkip.Casting : HuntSenseCastSkip.None;
    }

    /// <summary>還沒送出施放時每拍問：魔尋寶在冷卻就等它轉完；等的時候不補放的條件成立就不等、不放。</summary>
    /// <param name="recastLeft">魔尋寶剩下的冷卻（秒）；NaN＝讀不到。</param>
    /// <param name="waitedMs">已經等冷卻多久；第一次看到冷卻給 0。</param>
    /// <param name="skip">回 <see cref="HuntSenseCooldown.GiveUp"/> 時的原因；其餘 None。</param>
    public static HuntSenseCooldown CooldownStep(float recastLeft, long waitedMs, bool known, bool participating, bool inCombat, bool casting, out HuntSenseCastSkip skip)
    {
        skip = HuntSenseCastSkip.None;
        if (recastLeft <= 0f)
        {
            return HuntSenseCooldown.Ready;
        }

        if (float.IsNaN(recastLeft) || recastLeft * 1000f > CooldownWaitMaxMs)
        {
            skip = HuntSenseCastSkip.CooldownUnknown;
        }
        else if (known)
        {
            skip = HuntSenseCastSkip.Known;
        }
        else if (participating)
        {
            skip = HuntSenseCastSkip.Participating;
        }
        else if (inCombat)
        {
            skip = HuntSenseCastSkip.InCombat;
        }
        else if (casting)
        {
            skip = HuntSenseCastSkip.Casting;
        }
        else if (waitedMs >= CooldownWaitMaxMs)
        {
            skip = HuntSenseCastSkip.Cooldown;
        }

        return skip == HuntSenseCastSkip.None ? HuntSenseCooldown.Cooling : HuntSenseCooldown.GiveUp;
    }

    /// <param name="elapsedMs">從這一步第一次呼叫起的時間；等冷卻的那段不算。</param>
    /// <param name="onFreelancer">目前的輔助職業是輔助自由人；不是就不送、也不看冷卻。</param>
    /// <param name="sent">已經送出過施放。</param>
    /// <param name="recastLeft">魔尋寶剩下的冷卻（秒）。</param>
    /// <param name="tryNow">這一拍要送出施放。</param>
    public static HuntSenseCastStep CastStep(long elapsedMs, bool onFreelancer, bool sent, long sinceLastTryMs, float recastLeft, out bool tryNow)
    {
        tryNow = false;
        if (!onFreelancer)
        {
            return elapsedMs >= CastGiveUpMs ? HuntSenseCastStep.NotSwitched : HuntSenseCastStep.Waiting;
        }

        if (sent && recastLeft > 0f)
        {
            return HuntSenseCastStep.Sent;
        }

        if (elapsedMs >= CastGiveUpMs)
        {
            return sent ? HuntSenseCastStep.NotCast : HuntSenseCastStep.Cooldown;
        }

        tryNow = recastLeft <= 0f && (!sent || sinceLastTryMs >= CastRetryMs);
        return HuntSenseCastStep.Waiting;
    }

    /// <returns>true＝不用再等：收到結果（序號變了）或等滿 <see cref="SenseWaitMs"/>。</returns>
    public static bool SenseWaitDone(int sequenceAtCast, int sequenceNow, long sinceCastMs, out bool timedOut)
    {
        timedOut = false;
        if (sequenceNow != sequenceAtCast)
        {
            return true;
        }

        timedOut = sinceCastMs >= SenseWaitMs;
        return timedOut;
    }

    /// <returns>true＝巡迴不再等補放（送出後超過 <see cref="HoldLimitMs"/>）。</returns>
    public static bool HoldExpired(long sinceSubmitMs)
    {
        return sinceSubmitMs >= HoldLimitMs;
    }
}
