using System.Numerics;
using BOCCHI.Pathfinding;

namespace BOCCHI.Chains;

public enum ReturnHoldDecision : byte
{
    /// <summary>沒有在讓位：照常施放。</summary>
    Proceed,

    /// <summary>玩家停下了（沒騎坐騎、靜止 <see cref="ReturnTakeover.QuietMs"/>）：讓位結束，接著施放。</summary>
    Settled,

    /// <summary>玩家還在動，或詠唱、跳躍、讀圖、導航在走：先不施放。</summary>
    Wait,

    /// <summary>玩家騎著坐騎：這次不返回（不拉他下來）。</summary>
    GiveUpMounted,

    /// <summary>等了 <see cref="ReturnTakeover.WaitLimitMs"/> 玩家都沒停下：這次不返回。</summary>
    GiveUpTimeout,
}

/// <summary>
/// 返回被玩家接手（施放後自己移動、或騎上坐騎）之後的讓位。純邏輯、不碰遊戲（離線驗收直接呼叫）；只在框架執行緒用。
/// 接手後 <see cref="HoldMs"/> 內（等待中玩家還在動就往後延），施放前先等玩家停下；騎著坐騎就這次不返回，
/// 一次等超過 <see cref="WaitLimitMs"/> 也不返回。所有返回動作鏈共用一份：非法模式待命時每幾秒就會再送一條返回。
/// </summary>
public sealed class ReturnTakeover
{
    public const long HoldMs = 60_000;

    public const long QuietMs = ManualMoveDetector.ResumeQuietMs;

    public const long WaitLimitMs = ManualMoveDetector.MaxYieldMs;

    /// <summary>離錨點（只看 X/Z）超過這個距離才算在動。</summary>
    public const float StillM = ReturnInterruptions.MovedThresholdM;

    private long takenAt;

    private long waitStartedAt;

    private long stillSince;

    private Vector3 anchor;

    private bool anchored;

    private bool giveUpReported;

    public bool Active(long now)
    {
        return takenAt != 0 && now - takenAt < HoldMs;
    }

    /// <returns>true＝新的一次接手（之前沒有或已過期）：呼叫端只在這時跟玩家說一次。</returns>
    public bool Note(long now)
    {
        var fresh = !Active(now);
        takenAt = now;
        if (fresh)
        {
            giveUpReported = false;
        }

        return fresh;
    }

    public void Clear()
    {
        takenAt = 0;
    }

    /// <summary>施放前（下坐騎前）先問：還沒在讓位、而使用者最近在操作，就當成已被接手，不必等施放失敗。</summary>
    /// <returns>true＝這一拍開始讓位：呼叫端寫一行記錄，接著照 <see cref="Step"/> 等他停下。</returns>
    public bool TakeOverIfHandsOn(long now, bool handsOn)
    {
        if (!handsOn || Active(now))
        {
            return false;
        }

        Note(now);
        return true;
    }

    /// <summary>戰鬥中原地等的時間不算：讓位的期限與等玩家停下的上限一起往後延，靜止從頭算。</summary>
    public void Hold(long heldMs)
    {
        if (heldMs <= 0)
        {
            return;
        }

        if (takenAt != 0)
        {
            takenAt += heldMs;
        }

        waitStartedAt += heldMs;
        anchored = false;
    }

    public void BeginWait(long now)
    {
        waitStartedAt = now;
        stillSince = 0;
        anchored = false;
    }

    /// <summary>這一次接手裡第一次放棄回 true（寫 Information），之後回 false（寫 Debug）。</summary>
    public bool TakeGiveUpReport()
    {
        var first = !giveUpReported;
        giveUpReported = true;
        return first;
    }

    /// <param name="mounted">騎乘中、上下坐騎中或正在叫坐騎。</param>
    /// <param name="busy">詠唱、跳躍、讀圖、vnavmesh 在走：不算靜止，也不算玩家在動。</param>
    public ReturnHoldDecision Step(long now, Vector3 player, bool mounted, bool busy)
    {
        if (!Active(now))
        {
            return ReturnHoldDecision.Proceed;
        }

        if (mounted)
        {
            return ReturnHoldDecision.GiveUpMounted;
        }

        if (busy)
        {
            anchored = false;
        }
        else if (!anchored)
        {
            anchor = player;
            anchored = true;
            stillSince = now;
        }
        else if (Vector2.Distance(new Vector2(anchor.X, anchor.Z), new Vector2(player.X, player.Z)) > StillM)
        {
            anchor = player;
            stillSince = now;
            takenAt = now;
        }
        else if (now - stillSince >= QuietMs)
        {
            Clear();
            return ReturnHoldDecision.Settled;
        }

        return now - waitStartedAt >= WaitLimitMs ? ReturnHoldDecision.GiveUpTimeout : ReturnHoldDecision.Wait;
    }
}
