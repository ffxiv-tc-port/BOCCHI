namespace BOCCHI.Modules.Teleporter;

/// <summary>
/// 代按返回確認框「是」之前，等自己的詠唱與動作硬直（動畫鎖）結束：硬直中按下去，遊戲不會開始詠唱返回。
/// 最多等 <see cref="MaxWaitMs"/>，逾時照舊按（與沒有這一步時相同）。純邏輯、不碰遊戲；只在框架執行緒用。
/// </summary>
public sealed class ReturnPressGate
{
    public const long MaxWaitMs = 1000;

    private long since = -1;

    /// <summary>這一次確認框有等過（給記錄用）。</summary>
    public bool Waited { get; private set; }

    /// <summary>等滿上限仍在詠唱／硬直，照樣按。</summary>
    public bool TimedOut { get; private set; }

    public long WaitedMs { get; private set; }

    /// <summary>開始等的那一刻看到的硬直秒數與是否在詠唱（給記錄用）。</summary>
    public float FirstLock { get; private set; }

    public bool FirstCasting { get; private set; }

    /// <summary>新的確認框比中時呼叫。</summary>
    public void Reset()
    {
        since = -1;
        Waited = TimedOut = FirstCasting = false;
        WaitedMs = 0;
        FirstLock = 0f;
    }

    /// <returns>true＝現在按；false＝這一幀先不按。</returns>
    public bool Ready(long now, bool casting, float animationLock)
    {
        var busy = casting || animationLock > 0f;
        if (!busy)
        {
            WaitedMs = since < 0 ? 0 : now - since;
            return true;
        }

        if (since < 0)
        {
            since = now;
            Waited = true;
            FirstLock = animationLock;
            FirstCasting = casting;
        }

        WaitedMs = now - since;
        if (WaitedMs < MaxWaitMs)
        {
            return false;
        }

        TimedOut = true;
        return true;
    }
}
