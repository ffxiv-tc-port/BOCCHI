namespace BOCCHI.Modules.Automator;

public enum RepairButtonVerdict
{
    /// <summary>還沒準備好：下一拍再看。</summary>
    Wait,

    /// <summary>按鈕已連續 <see cref="RepairButtonGate.StableFrames"/> 拍可按：這一拍按。</summary>
    Press,

    /// <summary>開始後 <see cref="RepairButtonGate.OpenTimeoutMs"/> 內修理視窗一次都沒就緒。</summary>
    WindowTimeout,

    /// <summary>視窗第一次就緒後 <see cref="RepairButtonGate.ButtonTimeoutMs"/> 內按鈕都沒有穩定可按。</summary>
    ButtonTimeout,
}

/// <summary>
/// 「全部修理」的按鈕閘門：視窗剛就緒那幾拍按鈕可能還沒變成可按，所以「不能按」只算還沒準備好，
/// 要連續 <see cref="StableFrames"/> 拍可按才按，過了時限才放棄。純邏輯、不碰遊戲、不配置；只在框架執行緒用。
/// </summary>
public sealed class RepairButtonGate
{
    public const int StableFrames = 5;

    public const long OpenTimeoutMs = 5_000;

    public const long ButtonTimeoutMs = 5_000;

    private long startedAt;

    private long readyAt;

    public int ReadyFrames { get; private set; }

    public int EnabledFrames { get; private set; }

    public int Streak { get; private set; }

    public int BestStreak { get; private set; }

    /// <summary>最後一拍視窗是不是就緒（逾時記錄用：開了又關 vs 開著但按鈕不能按）。</summary>
    public bool WindowReadyNow { get; private set; }

    /// <summary>從開始（送出「修理」）到視窗第一次就緒的毫秒數；還沒就緒是 -1。</summary>
    public long OpenedAfterMs
    {
        get => readyAt == 0 ? -1 : readyAt - startedAt;
    }

    public void Begin(long now)
    {
        startedAt = now;
        readyAt = 0;
        ReadyFrames = 0;
        EnabledFrames = 0;
        Streak = 0;
        BestStreak = 0;
        WindowReadyNow = false;
    }

    /// <summary>從視窗第一次就緒到 <paramref name="now"/> 的毫秒數；還沒就緒是 -1。</summary>
    public long SinceReadyMs(long now)
    {
        return readyAt == 0 ? -1 : now - readyAt;
    }

    public RepairButtonVerdict Observe(long now, bool windowReady, bool buttonEnabled)
    {
        WindowReadyNow = windowReady;
        if (windowReady)
        {
            if (readyAt == 0)
            {
                readyAt = now;
            }

            ReadyFrames++;
            if (buttonEnabled)
            {
                EnabledFrames++;
                Streak++;
                if (Streak > BestStreak)
                {
                    BestStreak = Streak;
                }

                if (Streak >= StableFrames)
                {
                    return RepairButtonVerdict.Press;
                }
            }
            else
            {
                Streak = 0;
            }
        }
        else
        {
            Streak = 0;
        }

        if (readyAt == 0)
        {
            return now - startedAt > OpenTimeoutMs ? RepairButtonVerdict.WindowTimeout : RepairButtonVerdict.Wait;
        }

        return now - readyAt > ButtonTimeoutMs ? RepairButtonVerdict.ButtonTimeout : RepairButtonVerdict.Wait;
    }
}
