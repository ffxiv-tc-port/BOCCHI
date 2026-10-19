using System.Numerics;

namespace BOCCHI.Pathfinding;

/// <summary>
/// 點在網格接不上的低台上、要跳上去：Approach＝地面上起跳前站的點（主網格上），Landing＝台上的落點（起跳後直線往這裡走），
/// 高度至少 <see cref="TopY"/>、離點水平 <see cref="TopRadius"/> 米內＝站在台上。
/// </summary>
public readonly record struct HuntLedge(Vector3 Approach, Vector3 Landing, float TopY, float TopRadius)
{
    public bool IsOnTop(Vector3 player, Vector3 target)
    {
        return player.Y >= TopY && LedgeClimb.Flat(player, target) <= TopRadius;
    }
}

public enum LedgeAction
{
    /// <summary>站在台上：照常走到點。</summary>
    Proceed,

    /// <summary>這一拍什麼都不做。</summary>
    Wait,

    /// <summary>用一般尋路走到 <see cref="HuntLedge.Approach"/>。</summary>
    Approach,

    /// <summary>到了起跳點：停下導航，等角色靜止。</summary>
    Stop,

    /// <summary>不尋路、直線往 <see cref="HuntLedge.Landing"/> 走。</summary>
    RunUp,

    /// <summary>施放跳躍；呼叫端接著要呼叫 <see cref="LedgeClimb.JumpSent"/>。</summary>
    Jump,

    /// <summary>剛站上台：停下直線移動，之後照常走到點。</summary>
    Landed,

    /// <summary>這一次沒上去（原因在 <see cref="LedgeClimb.LastFailure"/>）：停下，從起跳點重來。</summary>
    Retry,

    /// <summary>試滿 <see cref="LedgeClimb.MaxAttempts"/> 次都沒上去：這一步跳過。</summary>
    GiveUp,
}

/// <summary>
/// 跳上低台：走到起跳點→停下等靜止→直線往台上走→走出 <see cref="RunUpM"/> 米（或 <see cref="RunUpMaxMs"/>）就跳→落地後看是不是站在台上；
/// 沒上去就從起跳點重來，最多 <see cref="MaxAttempts"/> 次。巡迴換步（stepKey 變了）就從頭算。
/// 純邏輯、不碰遊戲、不配置；只在框架執行緒用，時間一律是呼叫端給的毫秒數。
/// </summary>
public sealed class LedgeClimb
{
    public const float ApproachReachM = 1.5f;

    /// <summary>角色在 <see cref="StillMs"/> 內水平移動不超過這麼多才算靜止。</summary>
    public const float StillM = 0.15f;

    public const long StillMs = 400;

    public const float RunUpM = 2f;

    public const long RunUpMaxMs = 800;

    /// <summary>送出跳躍後至少等這麼久才判落地（跳躍狀態可能晚一兩幀才出現）。</summary>
    public const long AirMinMs = 400;

    /// <summary>送出跳躍後一直沒看到跳躍狀態，等到這麼久就當成已落地來判。</summary>
    public const long AirMaxMs = 2000;

    public const long LandSettleMs = 250;

    public const int MaxAttempts = 3;

    private enum Phase
    {
        Approach,
        Settle,
        RunUp,
        Jumping,
        Air,
        Done,
        GaveUp,
    }

    private Phase phase;

    private int stepKey = int.MinValue;

    private Vector3 still;

    private long stillSince;

    private Vector3 runFrom;

    private long runSince;

    private long jumpAt;

    private bool sawJumping;

    private long landedAt = -1;

    public int Attempts { get; private set; }

    /// <summary>最近一次沒上去的原因；給記錄用。</summary>
    public string LastFailure { get; private set; } = string.Empty;

    public void Reset()
    {
        phase = Phase.Approach;
        stepKey = int.MinValue;
        Attempts = 0;
        LastFailure = string.Empty;
        landedAt = -1;
    }

    /// <param name="stepKey">巡迴這一步的識別（步驟索引）；跟上一次不同就從頭算。</param>
    /// <param name="onTop">角色現在站在台上（<see cref="HuntLedge.IsOnTop"/>）。</param>
    /// <param name="jumping">遊戲的跳躍狀態。</param>
    /// <param name="holding">巡迴正在讓位給手動移動：還沒起跳就先不跳、回到走向起跳點。</param>
    public LedgeAction Step(long now, int stepKey, Vector3 player, bool onTop, bool jumping, bool holding, HuntLedge ledge)
    {
        if (stepKey != this.stepKey)
        {
            Reset();
            this.stepKey = stepKey;
        }

        switch (phase)
        {
            case Phase.GaveUp:
                return LedgeAction.GiveUp;
            case Phase.Done:
                if (onTop)
                {
                    return LedgeAction.Proceed;
                }

                phase = Phase.Approach;
                return LedgeAction.Approach;
            case Phase.Approach:
                if (onTop)
                {
                    phase = Phase.Done;
                    return LedgeAction.Proceed;
                }

                if (holding || Flat(player, ledge.Approach) > ApproachReachM)
                {
                    return LedgeAction.Approach;
                }

                phase = Phase.Settle;
                still = player;
                stillSince = now;
                return LedgeAction.Stop;
            case Phase.Settle:
                if (holding || Flat(player, ledge.Approach) > ApproachReachM * 2)
                {
                    phase = Phase.Approach;
                    return LedgeAction.Approach;
                }

                if (Flat(player, still) > StillM)
                {
                    still = player;
                    stillSince = now;
                    return LedgeAction.Wait;
                }

                if (now - stillSince < StillMs)
                {
                    return LedgeAction.Wait;
                }

                phase = Phase.RunUp;
                runFrom = player;
                runSince = now;
                return LedgeAction.RunUp;
            case Phase.RunUp:
                if (Flat(player, runFrom) < RunUpM && now - runSince < RunUpMaxMs)
                {
                    return LedgeAction.Wait;
                }

                phase = Phase.Jumping;
                return LedgeAction.Jump;
            case Phase.Jumping:
                return LedgeAction.Wait;
            default:
                return StepAir(now, onTop, jumping, player);
        }
    }

    /// <summary>呼叫端施放跳躍之後告訴結果：false（沒送出去）算這一次沒上去。</summary>
    public LedgeAction JumpSent(bool ok, long now)
    {
        if (phase != Phase.Jumping)
        {
            return LedgeAction.Wait;
        }

        if (!ok)
        {
            return Fail("跳躍沒有送出去");
        }

        phase = Phase.Air;
        jumpAt = now;
        sawJumping = false;
        landedAt = -1;
        return LedgeAction.Wait;
    }

    private LedgeAction StepAir(long now, bool onTop, bool jumping, Vector3 player)
    {
        if (jumping)
        {
            sawJumping = true;
            landedAt = -1;
            return LedgeAction.Wait;
        }

        var elapsed = now - jumpAt;
        if (elapsed < AirMinMs || (!sawJumping && elapsed < AirMaxMs))
        {
            return LedgeAction.Wait;
        }

        if (landedAt < 0)
        {
            landedAt = now;
        }

        if (now - landedAt < LandSettleMs)
        {
            return LedgeAction.Wait;
        }

        if (onTop)
        {
            phase = Phase.Done;
            return LedgeAction.Landed;
        }

        return Fail(sawJumping
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"落地後不在台上（高度 {player.Y:F2}）")
            : "送出跳躍後一直沒看到跳躍狀態");
    }

    private LedgeAction Fail(string why)
    {
        Attempts++;
        LastFailure = why;
        if (Attempts >= MaxAttempts)
        {
            phase = Phase.GaveUp;
            return LedgeAction.GiveUp;
        }

        phase = Phase.Approach;
        return LedgeAction.Retry;
    }

    public static float Flat(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return System.MathF.Sqrt(dx * dx + dz * dz);
    }
}
