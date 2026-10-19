using BOCCHI.Modules.Automator;

namespace BOCCHI.Modules.Teleporter;

public enum PotReturnStart : byte
{
    /// <summary>照舊：現在送返回。</summary>
    ReturnNow,

    /// <summary>已經在等：這次的返回等這一輪等完再一起送。</summary>
    Absorb,

    /// <summary>離開魔法甕：先看它是不是結束了，結束的話等 EurekaHelper 尋寶。</summary>
    WaitAfterPot,

    /// <summary>別的活動結束時 EurekaHelper 正在尋寶：等它找完。</summary>
    WaitWhileHunting,
}

public enum PotReturnVerdict : byte
{
    /// <summary>沒有在等。</summary>
    Idle,

    /// <summary>先不送返回。</summary>
    Hold,

    /// <summary>等完了：現在送返回。</summary>
    ReturnNow,

    /// <summary>離開魔法甕 <see cref="PotReturnWait.EndConfirmMs"/> 後它仍在 F.A.T.E. 清單上（只是走出範圍）：照舊送返回。</summary>
    ReturnNotEnded,
}

/// <summary>
/// 「打完自動返回」在魔法甕之後先讓 EurekaHelper 的尋寶走完。純邏輯、不碰遊戲（離線驗收直接呼叫）；只在框架執行緒用。
/// 等待窗、上限、問不到一律放行都交給 <see cref="PotHuntYield"/>；這裡只多「離開魔法甕範圍時它結束了沒」這一段。
/// 尋寶結束後要不要先等使用者開財寶箱由呼叫端決定（<see cref="ArmChest"/>），等的規則同非法模式（<see cref="PotChestSettle"/>）。
/// </summary>
public sealed class PotReturnWait
{
    /// <summary>離開魔法甕範圍後最多等這麼久看它離開 F.A.T.E. 清單（實機打完時兩者差不到 0.1 秒）。</summary>
    public const long EndConfirmMs = 1_000;

    private readonly PotHuntYield yield = new();

    private readonly PotChestSettle chest = new();

    private bool confirming;

    private long confirmSince;

    private bool waiting;

    public bool IsPending
    {
        get => confirming || waiting || chest.IsArmed;
    }

    /// <summary>尋寶結束後在等使用者開財寶箱。</summary>
    public bool IsSettlingChest
    {
        get => chest.IsArmed;
    }

    /// <summary>這一輪等財寶箱時看過財寶箱或互動。</summary>
    public bool ChestSeen
    {
        get => chest.SawChest;
    }

    public bool IsConfirming
    {
        get => confirming;
    }

    public bool IsHunting
    {
        get => yield.IsHunting;
    }

    /// <param name="available">問得到 EurekaHelper.OccultPotHuntActive；false 時一律照舊返回（與沒有這個端點時相同）。</param>
    public static PotReturnStart Decide(bool pending, bool isPot, bool available, bool active)
    {
        if (pending)
        {
            return PotReturnStart.Absorb;
        }

        if (!available)
        {
            return PotReturnStart.ReturnNow;
        }

        if (isPot)
        {
            return PotReturnStart.WaitAfterPot;
        }

        return active ? PotReturnStart.WaitWhileHunting : PotReturnStart.ReturnNow;
    }

    /// <returns>false＝已經在等，不理。</returns>
    public bool BeginAfterPot(long now)
    {
        if (IsPending)
        {
            return false;
        }

        yield.Reset();
        confirming = true;
        confirmSince = now;
        return true;
    }

    /// <returns>false＝已經在等，不理。</returns>
    public bool BeginWhileHunting()
    {
        if (IsPending)
        {
            return false;
        }

        yield.Reset();
        waiting = true;
        return true;
    }

    public void Cancel()
    {
        confirming = false;
        waiting = false;
        yield.Reset();
        chest.Reset();
    }

    /// <summary><see cref="Step"/> 因尋寶結束放行之後呼叫：先不送返回，改等使用者開完挖出來的財寶箱。</summary>
    public void ArmChest(long now)
    {
        confirming = false;
        waiting = false;
        yield.Reset();
        chest.Arm(now);
    }

    /// <summary>等財寶箱時 EurekaHelper 又開始尋寶（第二處財寶）：改回等它找完；找完由呼叫端再 <see cref="ArmChest"/>。</summary>
    public void ResumeHunting()
    {
        chest.Reset();
        confirming = false;
        yield.Reset();
        waiting = true;
    }

    public PotReturnVerdict StepChest(long now, bool busy, out PotChestSettleEvent ev, out long heldMs)
    {
        return StepChest(now, busy, false, 0, out ev, out heldMs);
    }

    /// <param name="busy">附近有魔法甕財寶箱，或在開箱、對話一類的互動事件裡。</param>
    /// <param name="secondSeen">收過「第二處財寶」；規則見 <see cref="PotChestSettle.Step(long, bool, bool, long, out PotChestSettleEvent, out long)"/>。</param>
    /// <returns>Hold＝還在等；ReturnNow＝等完（<paramref name="ev"/> 說明是安靜放行還是到上限）；沒在等時 Idle。</returns>
    public PotReturnVerdict StepChest(long now, bool busy, bool secondSeen, long secondSeenAt, out PotChestSettleEvent ev, out long heldMs)
    {
        if (!chest.IsArmed)
        {
            ev = PotChestSettleEvent.None;
            heldMs = 0;
            return PotReturnVerdict.Idle;
        }

        return chest.Step(now, busy, secondSeen, secondSeenAt, out ev, out heldMs) ? PotReturnVerdict.Hold : PotReturnVerdict.ReturnNow;
    }

    /// <param name="potListed">離開的那個魔法甕還在 F.A.T.E. 清單上。</param>
    /// <param name="available">這次問得到 EurekaHelper.OccultPotHuntActive。</param>
    /// <param name="active">它回的值；<paramref name="available"/> 為 false 時不看。</param>
    /// <param name="heldMs">放行時已經等了多久（沒有事件時是 0；走出範圍時是確認的時間）。</param>
    public PotReturnVerdict Step(long now, bool potListed, bool available, bool active, out PotHuntEvent ev, out long heldMs)
    {
        ev = PotHuntEvent.None;
        heldMs = 0;
        if (confirming)
        {
            if (potListed)
            {
                if (now - confirmSince < EndConfirmMs)
                {
                    return PotReturnVerdict.Hold;
                }

                confirming = false;
                heldMs = now - confirmSince;
                return PotReturnVerdict.ReturnNotEnded;
            }

            // 等待窗從魔法甕離開清單那一刻起算，與非法模式相同。
            confirming = false;
            waiting = true;
            yield.ArmGrace(now);
        }

        if (!waiting)
        {
            return PotReturnVerdict.Idle;
        }

        if (yield.Step(now, available, active, out ev, out heldMs))
        {
            return PotReturnVerdict.Hold;
        }

        waiting = false;
        yield.Reset();
        return PotReturnVerdict.ReturnNow;
    }
}
