namespace BOCCHI.Modules.Automator;

public enum PotChestSettleEvent : byte
{
    None,

    /// <summary>連續 <see cref="PotChestSettle.QuietMs"/> 附近沒有財寶箱、也沒在互動：不再等。</summary>
    Quiet,

    /// <summary>等滿 <see cref="PotChestSettle.CapMs"/>：不再等。</summary>
    CapReached,

    /// <summary>收到「第二處財寶」：改等 EurekaHelper 開始下一輪（最多 <see cref="PotChestSettle.SecondTreasureWaitMs"/>）。</summary>
    SecondTreasure,

    /// <summary>收到「第二處財寶」後等滿 <see cref="PotChestSettle.SecondTreasureWaitMs"/> 都沒開始下一輪：不再等。</summary>
    SecondTreasureExpired,
}

/// <summary>
/// 魔法甕尋寶結束後，先等使用者開完挖出來的財寶箱再選下一個活動：附近有財寶箱或在互動事件裡就等，
/// 連續 <see cref="QuietMs"/>（這一輪看過財寶箱或互動時 <see cref="QuietAfterChestMs"/>）都沒有才放行，最多等 <see cref="CapMs"/>。
/// 這一輪收到「第二處財寶」就改等 EurekaHelper 開始下一輪（開始後由呼叫端的 <see cref="PotHuntYield"/> 接手），最多 <see cref="SecondTreasureWaitMs"/>。
/// 純邏輯、不碰遊戲（離線驗收直接呼叫）；只在框架執行緒用。時間一律用呼叫端給的 now，狀態另用旗標，不拿 0 當「沒有」。
/// </summary>
public sealed class PotChestSettle
{
    public const long QuietMs = 3_000;

    /// <summary>看過財寶箱或互動之後的安靜時間：「第二處財寶」訊息在開完財寶箱後約 8.1 秒才到。</summary>
    public const long QuietAfterChestMs = 10_000;

    /// <summary>收到「第二處財寶」後等 EurekaHelper 開始下一輪的上限（實機 0.8～5.0 秒）。</summary>
    public const long SecondTreasureWaitMs = 10_000;

    public const long CapMs = 45_000;

    /// <summary>財寶箱離角色這麼近（米）才算「附近」。</summary>
    public const float NearbyM = 30f;

    // 魔法甕挖出來的黃金／白銀／青銅財寶箱（EventObj，與幸運胡蘿蔔的黃金財寶箱同一個事件）。
    private static readonly uint[] ChestBaseIds = [2014741, 2014742, 2014743];

    private bool armed;

    private long armedAt;

    private bool quiet;

    private long quietSince;

    private bool second;

    private long secondAt;

    public bool IsArmed
    {
        get => armed;
    }

    /// <summary>這一輪等待中看過財寶箱或互動。</summary>
    public bool SawChest { get; private set; }

    public static bool IsPotChest(uint baseId)
    {
        foreach (var id in ChestBaseIds)
        {
            if (id == baseId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>尋寶結束（EurekaHelper 變回 false）時呼叫，重新開始一輪等待。</summary>
    public void Arm(long now)
    {
        armed = true;
        armedAt = now;
        quiet = false;
        second = false;
        SawChest = false;
    }

    public void Reset()
    {
        armed = false;
        quiet = false;
        second = false;
        SawChest = false;
    }

    public bool Step(long now, bool busy, out PotChestSettleEvent ev, out long heldMs)
    {
        return Step(now, busy, false, 0, out ev, out heldMs);
    }

    /// <param name="busy">附近 <see cref="NearbyM"/> 米內有可選取的財寶箱，或在開箱、對話一類的互動事件裡。</param>
    /// <param name="secondSeen">收過「第二處財寶」；<paramref name="secondSeenAt"/> 是最近一次的時間，比這一輪 <see cref="Arm"/> 早的不算。</param>
    /// <param name="heldMs">事件發生時已經等了多久（沒有事件時是 0）。</param>
    /// <returns>true＝先不選下一個活動、不出發。</returns>
    public bool Step(long now, bool busy, bool secondSeen, long secondSeenAt, out PotChestSettleEvent ev, out long heldMs)
    {
        ev = PotChestSettleEvent.None;
        heldMs = 0;
        if (!armed)
        {
            return false;
        }

        if (now - armedAt >= CapMs)
        {
            armed = false;
            ev = PotChestSettleEvent.CapReached;
            heldMs = now - armedAt;
            return false;
        }

        if (!second && secondSeen && secondSeenAt >= armedAt)
        {
            second = true;
            secondAt = secondSeenAt;
            ev = PotChestSettleEvent.SecondTreasure;
            heldMs = now - armedAt;
            return true;
        }

        if (second)
        {
            if (now - secondAt < SecondTreasureWaitMs)
            {
                return true;
            }

            armed = false;
            ev = PotChestSettleEvent.SecondTreasureExpired;
            heldMs = now - armedAt;
            return false;
        }

        if (busy)
        {
            SawChest = true;
            quiet = false;
            return true;
        }

        if (!quiet)
        {
            quiet = true;
            quietSince = now;
        }

        if (now - quietSince < (SawChest ? QuietAfterChestMs : QuietMs))
        {
            return true;
        }

        armed = false;
        ev = PotChestSettleEvent.Quiet;
        heldMs = now - armedAt;
        return false;
    }
}
