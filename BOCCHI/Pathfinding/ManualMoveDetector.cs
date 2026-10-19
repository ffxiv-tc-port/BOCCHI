using System;
using System.Numerics;

namespace BOCCHI.Pathfinding;

public enum TravelYieldEvent
{
    None,

    /// <summary>導航停著、角色持續自己移動滿 <see cref="ManualMoveDetector.ConfirmMs"/>：開始讓位。</summary>
    Started,

    /// <summary>讓位中角色靜止滿 <see cref="ManualMoveDetector.ResumeQuietMs"/>：該從目前位置接續了。</summary>
    Resumed,

    /// <summary>讓位滿 <see cref="ManualMoveDetector.MaxYieldMs"/>：這一趟持續移動不再讓位，回到原本的停下重送／中止重來（按鍵／互動讓位照常、額度內）。</summary>
    Expired,

    /// <summary>讓位中導航又動了（別的地方送了移動）：不必接續。</summary>
    Cancelled,

    /// <summary>Lifestream 開始執行任務：讓位到它做完（呼叫端先停下自己的導航，否則 Lifestream 會中止任務）。</summary>
    External,

    /// <summary>為 Lifestream 讓位累計滿 <see cref="ManualMoveDetector.ExternalMaxMs"/>：這一趟之後不再為它讓位。</summary>
    ExternalLimit,

    /// <summary>導航停著、角色沒在動，但使用者剛按過移動鍵或在互動事件裡（短按、開寶箱）：開始讓位，不必等 <see cref="ManualMoveDetector.ConfirmMs"/>。</summary>
    Touched,

    /// <summary>這一趟為按鍵／互動讓位累計滿 <see cref="ManualMoveDetector.TouchMaxMs"/>：之後短按與互動不再讓位（持續移動照舊）。</summary>
    TouchLimit,
}

/// <param name="YieldedMs">上一個取樣到這一個之間處於讓位的毫秒數（活動帳累計用）。</param>
public readonly record struct TravelYieldStep(TravelYieldEvent Event, long YieldedMs, float Speed);

/// <summary>
/// 「導航停了、角色卻還在動」＝使用者在手動移動的判斷與讓位狀態。純邏輯、不碰遊戲、不配置；只在 framework 執行緒用。
/// 速度用水平位移除以約 <see cref="WindowMs"/> 的時間窗；導航中、上下坐騎、詠唱、跳躍、讀圖中的取樣不算手動。
/// 擊退與瞬移是一次性的：要連續 <see cref="ConfirmMs"/> 都在動才算手動，速度高到不可能是騎乘就當成瞬移、清掉歷史。
/// Lifestream 執行任務時（使用者在地圖上點目的地）它用自己的移動帶角色走、導航停著，同樣讓位，而且不算進上限。
/// 呼叫端另外告訴它「使用者剛碰過」（移動鍵、互動事件）時，導航停著就讓位、不等連續移動（短按停下導航、走去開寶箱）。
/// </summary>
public sealed class ManualMoveDetector
{
    public const long SampleIntervalMs = 100;

    public const long WindowMs = 300;

    /// <summary>算「在動」的水平速度（米／秒）：騎乘後退實測約 2.6、走路模式約 2.4 都要算手動；跑步約 6、新月島騎乘約 12～17。</summary>
    public const float MoveSpeedMin = 1.5f;

    /// <summary>算「靜止」的水平速度上限（米／秒）。介於靜止與在動之間的速度，讓位中維持讓位、沒讓位時照舊判斷停下。</summary>
    public const float StillSpeedMax = 1f;

    /// <summary>超過這個速度（米／秒）當成瞬移或傳送，不是移動。</summary>
    public const float TeleportSpeed = 50f;

    public const long ConfirmMs = 1500;

    public const long ResumeQuietMs = 1500;

    public const long MaxYieldMs = 30_000;

    /// <summary>一趟（到 <see cref="Reset"/> 為止）最多為 Lifestream 讓位這麼久；這段時間不算進 <see cref="MaxYieldMs"/>。</summary>
    public const long ExternalMaxMs = 180_000;

    /// <summary>一趟（到 <see cref="Reset"/> 為止）最多為按鍵／互動讓位這麼久：按鍵狀態讀錯時也不會一直不重送。</summary>
    public const long TouchMaxMs = 60_000;

    private const int Capacity = 8;

    private readonly long[] times = new long[Capacity];

    private readonly float[] xs = new float[Capacity];

    private readonly float[] zs = new float[Capacity];

    private int count;

    private int head;

    private long nextSampleAt;

    private long lastSampleAt;

    private long movingSince;

    private long stillSince;

    private long yieldStartedAt;

    // 這一次讓位裡為 Lifestream 等的時間（不算進上限）；這一趟為 Lifestream 讓位的累計。
    private long capPausedMs;

    private long externalMs;

    private bool externalActive;

    // 這一次讓位是按鍵／互動開始的；這一趟為它們讓位的累計。
    private bool touchYield;

    private long touchMs;

    private bool touchLimitReported;

    public bool Yielding
    {
        get => yieldStartedAt != 0;
    }

    /// <summary>
    /// 這一趟已經讓位滿上限、現在照常重送：之後只剩按鍵／互動讓位（額度內），那段讓位中與它待接續時是 false。
    /// 沒有按鍵／互動訊號時一直是 true，直到 <see cref="Reset"/>。
    /// </summary>
    public bool Expired
    {
        get => capped && !Yielding && !resumeDue;
    }

    /// <summary>這一趟讓位滿過上限（持續移動不再讓位）；直到 <see cref="Reset"/>。</summary>
    public bool Capped
    {
        get => capped;
    }

    private bool capped;

    /// <summary>最近一次取樣：導航停著、沒有在上下坐騎之類、而且水平速度不低於 <see cref="MoveSpeedMin"/>。</summary>
    public bool MovingNow { get; private set; }

    /// <summary>最近一次取樣正在為 Lifestream 讓位。</summary>
    public bool External
    {
        get => externalActive;
    }

    public void Reset()
    {
        count = 0;
        head = 0;
        nextSampleAt = 0;
        lastSampleAt = 0;
        movingSince = 0;
        stillSince = 0;
        yieldStartedAt = 0;
        capPausedMs = 0;
        externalMs = 0;
        externalActive = false;
        capped = false;
        MovingNow = false;
        resumeDue = false;
        touchYield = false;
        touchMs = 0;
        touchLimitReported = false;
    }

    /// <summary>按鍵／互動讓位的累計額度重新計算（巡迴換到下一步時）；其餘讓位狀態不動。</summary>
    public void ResetTouchBudget()
    {
        touchMs = 0;
        touchLimitReported = false;
    }

    private bool resumeDue;

    /// <summary>有待接續就回 true 並清掉（一次讓位只接續一次）。</summary>
    public bool TakeResume()
    {
        var due = resumeDue;
        resumeDue = false;
        return due;
    }

    /// <summary>不必先看到手動移動就開始讓位（之後照常接續或到期）；已在讓位或這一趟已到期時不動作。</summary>
    public void BeginYield(long now)
    {
        if (Yielding || capped)
        {
            return;
        }

        yieldStartedAt = now;
        movingSince = 0;
        stillSince = 0;
        capPausedMs = 0;
        resumeDue = false;
        touchYield = false;
    }

    public bool ShouldSample(long now)
    {
        if (now < nextSampleAt)
        {
            return false;
        }

        nextSampleAt = now + SampleIntervalMs;
        return true;
    }

    /// <param name="navigating">vnavmesh 在走或還在算路。</param>
    /// <param name="busy">上下坐騎、詠唱、跳躍、讀圖中。</param>
    public TravelYieldStep Observe(Vector3 player, bool navigating, bool busy, long now)
    {
        return Observe(player, navigating, busy, false, now);
    }

    /// <param name="external">
    /// Lifestream 正在執行任務：一律讓位（這一趟已讓位滿上限也重新讓位）、不接續、這段時間不算進上限，
    /// 一趟累計到 <see cref="ExternalMaxMs"/> 為止。false 時與沒有這個參數的版本逐拍相同。
    /// </param>
    public TravelYieldStep Observe(Vector3 player, bool navigating, bool busy, bool external, long now)
    {
        return Observe(player, navigating, busy, external, false, now);
    }

    /// <param name="touched">
    /// 使用者剛按過移動鍵，或在不是 BOCCHI 自己發起的互動事件裡：導航停著、角色沒在動時就開始讓位（<see cref="TravelYieldEvent.Touched"/>），
    /// 讓位中為 true 時不算靜止。一趟累計 <see cref="TouchMaxMs"/> 為止。false 時與沒有這個參數的版本逐拍相同。
    /// </param>
    public TravelYieldStep Observe(Vector3 player, bool navigating, bool busy, bool external, bool touched, long now)
    {
        var wasExternal = externalActive;
        externalActive = external && externalMs < ExternalMaxMs;
        if (capped)
        {
            if (externalActive)
            {
                // 已讓位滿上限、照常重送的這一趟也要讓：重送會讓 Lifestream 中止任務。
                capped = false;
                yieldStartedAt = 0;
            }
            else if (!Yielding && !touched)
            {
                resumeDue &= !navigating;
                return default;
            }
        }

        var yielded = Yielding ? now - Math.Max(lastSampleAt, yieldStartedAt) : 0;
        lastSampleAt = now;
        var speed = Push(player, now);

        if (externalActive)
        {
            MovingNow = false;
            movingSince = 0;
            stillSince = 0;
            resumeDue = false;
            touchYield = false;
            if (!Yielding)
            {
                yieldStartedAt = now;
                capPausedMs = 0;
                return new TravelYieldStep(TravelYieldEvent.External, 0, speed);
            }

            capPausedMs += yielded;
            externalMs += yielded;
            return new TravelYieldStep(wasExternal ? TravelYieldEvent.None : TravelYieldEvent.External, yielded, speed);
        }

        if (touchYield)
        {
            touchMs += yielded;
        }

        if (wasExternal && external)
        {
            return new TravelYieldStep(TravelYieldEvent.ExternalLimit, yielded, speed);
        }

        if (navigating)
        {
            MovingNow = false;
            movingSince = 0;
            resumeDue = false;
            if (!Yielding)
            {
                return new TravelYieldStep(TravelYieldEvent.None, 0, speed);
            }

            yieldStartedAt = 0;
            touchYield = false;
            return new TravelYieldStep(TravelYieldEvent.Cancelled, yielded, speed);
        }

        if (busy)
        {
            MovingNow = false;
            movingSince = 0;
            stillSince = 0;
            return new TravelYieldStep(TravelYieldEvent.None, yielded, speed);
        }

        MovingNow = speed >= MoveSpeedMin;
        if (!Yielding && capped)
        {
            // 讓位滿上限後持續移動不再讓位，按鍵／互動照常（按著移動鍵走也算）：否則每次重送都被 vnavmesh 的輸入取消打斷。
            MovingNow = false;
            movingSince = 0;
            return touched ? Touch(now, speed) : new TravelYieldStep(TravelYieldEvent.None, 0, speed);
        }

        if (!Yielding)
        {
            if (!MovingNow)
            {
                movingSince = 0;
                return touched ? Touch(now, speed) : new TravelYieldStep(TravelYieldEvent.None, 0, speed);
            }

            movingSince = movingSince == 0 ? now : movingSince;
            if (now - movingSince < ConfirmMs)
            {
                return new TravelYieldStep(TravelYieldEvent.None, 0, speed);
            }

            yieldStartedAt = now;
            stillSince = 0;
            capPausedMs = 0;
            touchYield = false;
            return new TravelYieldStep(TravelYieldEvent.Started, 0, speed);
        }

        if (now - yieldStartedAt - capPausedMs >= MaxYieldMs)
        {
            capped = true;
            yieldStartedAt = 0;
            touchYield = false;
            MovingNow = false;
            resumeDue = false;
            return new TravelYieldStep(TravelYieldEvent.Expired, yielded, speed);
        }

        if (touched || speed < 0 || speed >= StillSpeedMax)
        {
            stillSince = 0;
            return new TravelYieldStep(TravelYieldEvent.None, yielded, speed);
        }

        stillSince = stillSince == 0 ? now : stillSince;
        if (now - stillSince < ResumeQuietMs)
        {
            return new TravelYieldStep(TravelYieldEvent.None, yielded, speed);
        }

        yieldStartedAt = 0;
        touchYield = false;
        resumeDue = true;
        return new TravelYieldStep(TravelYieldEvent.Resumed, yielded, speed);
    }

    // 沒在讓位、角色沒在動、使用者剛碰過：額度內就開始讓位；用完只回報一次，之後照沒有這個訊號。
    private TravelYieldStep Touch(long now, float speed)
    {
        if (touchMs < TouchMaxMs)
        {
            yieldStartedAt = now;
            stillSince = 0;
            capPausedMs = 0;
            resumeDue = false;
            touchYield = true;
            return new TravelYieldStep(TravelYieldEvent.Touched, 0, speed);
        }

        if (touchLimitReported)
        {
            return new TravelYieldStep(TravelYieldEvent.None, 0, speed);
        }

        touchLimitReported = true;
        return new TravelYieldStep(TravelYieldEvent.TouchLimit, 0, speed);
    }

    // 記下這一筆，回傳到時間窗以前最近那一筆的水平速度；歷史不夠長回 -1（不知道）。瞬移時清掉歷史、回 -1。
    private float Push(Vector3 player, long now)
    {
        var speed = -1f;
        for (var i = 0; i < count; i++)
        {
            var index = (head - 1 - i + Capacity) % Capacity;
            if (now - times[index] < WindowMs)
            {
                continue;
            }

            var seconds = (now - times[index]) / 1000f;
            speed = MathF.Sqrt((player.X - xs[index]) * (player.X - xs[index]) + (player.Z - zs[index]) * (player.Z - zs[index])) / seconds;
            break;
        }

        if (speed > TeleportSpeed)
        {
            count = 0;
            speed = -1f;
        }

        times[head] = now;
        xs[head] = player.X;
        zs[head] = player.Z;
        head = (head + 1) % Capacity;
        count = Math.Min(count + 1, Capacity);
        return speed;
    }
}
