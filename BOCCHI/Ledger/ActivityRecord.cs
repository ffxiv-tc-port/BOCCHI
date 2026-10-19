namespace BOCCHI.Ledger;

/// <summary>
/// 一個非法模式活動的時間軸與計數（活動帳用）。時間一律是 Environment.TickCount64；0＝還沒發生。
/// 只在 framework 執行緒讀寫；只存數值，不存遊戲物件。
/// </summary>
public sealed class ActivityRecord
{
    public long SelectedAt;

    public long ArrivedAt;

    public long ParticipatingAt;

    /// <summary>趕路的動作鏈送出幾次；第二次起是中止後重來。</summary>
    public int PathfindingRuns;

    /// <summary>最後一次選的導航方式；沒有趕路（接手進行中的活動）是 null。</summary>
    public string? Navigation;

    /// <summary>最近一次趕路動作鏈開始的時間。</summary>
    public long PathfindingStartedAt;

    public bool TravelInterruptReported;

    /// <summary>趕路監看看到導航停了、在原地重送同一個目的地的次數（不算中止重來）。</summary>
    public int TravelRewalks;

    public bool TravelRewalkReported;

    /// <summary>趕路時讓位給手動移動的總毫秒數（跨中止重來累計）。</summary>
    public long TravelYieldMs;

    public bool TravelYieldReported;

    public bool TravelResumeReported;

    public bool TravelExternalReported;

    public bool TravelTouchReported;

    public bool TravelCappedTouchReported;

    /// <summary>-1＝不知道。</summary>
    public int ProgressAtArrival = -1;

    public void MarkArrived(long now, int progress)
    {
        if (ArrivedAt != 0)
        {
            return;
        }

        ArrivedAt = now;
        ProgressAtArrival = progress;
    }

    public void MarkParticipating(long now, int progress)
    {
        MarkArrived(now, progress);
        if (ParticipatingAt == 0)
        {
            ParticipatingAt = now;
        }
    }
}
