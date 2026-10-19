namespace BOCCHI.Pathfinding;

public enum HuntCombatEvent
{
    None,

    /// <summary>進入戰鬥：開始原地等（呼叫端停下自己的導航）。</summary>
    Started,

    /// <summary>脫離戰鬥滿 <see cref="HuntCombatWait.SettleMs"/>：結束等待，從目前位置接續這一步。</summary>
    Ended,

    /// <summary>等滿 <see cref="HuntCombatWait.MaxWaitMs"/> 還在戰鬥：這場戰鬥退回原本的做法。</summary>
    Expired,

    /// <summary>退回原本做法的那場戰鬥結束了；之後的戰鬥重新原地等。</summary>
    ExpiredOver,

    /// <summary>騎乘中進入戰鬥：不原地等、照常走（騎走會脫離戰鬥）；還在戰鬥時下了坐騎才開始等（<see cref="Started"/>）。</summary>
    Riding,
}

/// <param name="WaitedMs">從開始等到這一拍的毫秒數；Ended／Expired／ExpiredOver 時是總長。</param>
/// <param name="HeldMs">上一拍到這一拍之間處於等待的毫秒數（暫停「同一步太久」的計時用）。</param>
public readonly record struct HuntCombatStep(HuntCombatEvent Event, long WaitedMs, long HeldMs);

/// <summary>
/// 巡迴途中進入戰鬥就原地等：不送導航、不叫坐騎、不判斷手動移動，讓戰鬥 AI（或使用者）打完；脫離戰鬥滿 <see cref="SettleMs"/> 才接續。
/// 等滿 <see cref="MaxWaitMs"/> 還在戰鬥就退回原本的做法，直到脫離戰鬥滿 <see cref="SettleMs"/>。
/// 呼叫端給騎乘狀態時，騎乘中不開始等（騎著不會出招，原地等＝站著挨打）；還在戰鬥時下了坐騎才開始。
/// 純邏輯、不碰遊戲、不配置；只在 framework 執行緒用。
/// </summary>
public sealed class HuntCombatWait
{
    /// <summary>同「同一步太久沒完成」的上限：連打好幾隻的長戰鬥也不能中途收回（照常送導航會壓住戰鬥 AI 的走位）。</summary>
    public const long MaxWaitMs = 180_000;

    /// <summary>戰鬥旗標會短暫掉下又回來（下一隻接著打）：要連續脫離這麼久才算打完。</summary>
    public const long SettleMs = 2_000;

    private bool waiting;

    private bool expired;

    private long startedAt;

    private long lastAt;

    private bool calm;

    private long calmSince;

    /// <summary>正在原地等：這一拍不跑步驟。</summary>
    public bool Holding
    {
        get => waiting;
    }

    /// <summary>不用原地等的呼叫端判「已脫戰」：這一拍不在戰鬥，而且離最後一次看到戰鬥已滿 <see cref="SettleMs"/>。</summary>
    /// <param name="lastCombatAt">最後一次看到戰鬥旗標的時刻；0＝沒看過。</param>
    public static bool IsSettled(bool inCombat, long lastCombatAt, long now)
    {
        return !inCombat && (lastCombatAt == 0 || now - lastCombatAt >= SettleMs);
    }

    public void Reset()
    {
        waiting = false;
        expired = false;
        calm = false;
        riding = false;
    }

    // 這場戰鬥是騎乘中開始的、已回報過 Riding；脫離戰鬥就清掉。
    private bool riding;

    /// <param name="inCombat">這一拍的戰鬥旗標。</param>
    /// <param name="mounted">這一拍騎乘中：還沒開始等時不開始等（回報一次 <see cref="HuntCombatEvent.Riding"/>）；已在等或已退回原本做法時不看。</param>
    public HuntCombatStep Observe(bool inCombat, bool mounted, long now)
    {
        if (waiting || expired || !inCombat || !mounted)
        {
            riding &= inCombat;
            return Observe(inCombat, now);
        }

        if (riding)
        {
            return default;
        }

        riding = true;
        return new HuntCombatStep(HuntCombatEvent.Riding, 0, 0);
    }

    /// <param name="inCombat">這一拍的戰鬥旗標。</param>
    public HuntCombatStep Observe(bool inCombat, long now)
    {
        if (!waiting && !expired)
        {
            if (!inCombat)
            {
                return default;
            }

            waiting = true;
            startedAt = now;
            lastAt = now;
            calm = false;
            return new HuntCombatStep(HuntCombatEvent.Started, 0, 0);
        }

        var held = waiting ? now - lastAt : 0;
        lastAt = now;
        var waited = now - startedAt;
        if (inCombat)
        {
            calm = false;
            if (waiting && waited >= MaxWaitMs)
            {
                waiting = false;
                expired = true;
                return new HuntCombatStep(HuntCombatEvent.Expired, waited, held);
            }

            return new HuntCombatStep(HuntCombatEvent.None, waited, held);
        }

        if (!calm)
        {
            calm = true;
            calmSince = now;
        }

        if (now - calmSince < SettleMs)
        {
            return new HuntCombatStep(HuntCombatEvent.None, waited, held);
        }

        var ended = waiting ? HuntCombatEvent.Ended : HuntCombatEvent.ExpiredOver;
        waiting = false;
        expired = false;
        calm = false;
        return new HuntCombatStep(ended, waited, held);
    }
}
