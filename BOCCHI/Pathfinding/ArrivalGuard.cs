namespace BOCCHI.Pathfinding;

public enum ArrivalDecision
{
    /// <summary>還在走或還在算路，或剛停下、等一下再判斷。</summary>
    Wait,

    /// <summary>已經在目的地範圍內。</summary>
    Arrived,

    /// <summary>停下了卻還不夠近：重送一次同一個目的地。</summary>
    Rewalk,

    /// <summary>重走 <see cref="ArrivalGuard.MaxRewalks"/> 次仍到不了；Reset 之前一直回這個。</summary>
    GiveUp,
}

/// <summary>
/// 「走到目的地附近」的判斷。vnavmesh 停下（Path.IsRunning 為 false）不代表走到了：路徑提早結束、被別的東西停下、
/// 算路中途都會停。所以停下時重新量距離，不夠近就重走，有上限。純邏輯、不碰遊戲、不配置；只在 framework 執行緒用。
/// </summary>
public sealed class ArrivalGuard
{
    public const int MaxRewalks = 2;

    // 停下後要持續這麼久都沒在走才重走：換路段、重算路時會有短暫的空窗。
    public const long SettleMs = 500;

    // 問 vnavmesh 的間隔：每次都是 IPC，距離每幀量、狀態不必每幀問。
    public const long PollIntervalMs = 100;

    private bool stopped;

    private long stoppedSince;

    private long nextPollAt;

    public int Rewalks { get; private set; }

    public bool GaveUp { get; private set; }

    public void Reset()
    {
        stopped = false;
        nextPollAt = 0;
        Rewalks = 0;
        GaveUp = false;
    }

    /// <summary>有進展時呼叫：重走次數歸零，停下計時不動。</summary>
    public void ClearRewalks()
    {
        Rewalks = 0;
        GaveUp = false;
    }

    /// <summary>這一拍要不要問 vnavmesh；回 true 時已排好下一拍。</summary>
    public bool ShouldPoll(long now)
    {
        if (now < nextPollAt)
        {
            return false;
        }

        nextPollAt = now + PollIntervalMs;
        return true;
    }

    /// <param name="distance">角色到目的地的距離。</param>
    /// <param name="navigating">vnavmesh 在走（Path.IsRunning）或還在算路（SimpleMove 進行中）。</param>
    public ArrivalDecision Next(float distance, float arriveDistance, bool navigating, long now)
    {
        if (distance <= arriveDistance)
        {
            return ArrivalDecision.Arrived;
        }

        if (GaveUp)
        {
            return ArrivalDecision.GiveUp;
        }

        if (navigating)
        {
            stopped = false;
            return ArrivalDecision.Wait;
        }

        if (!stopped)
        {
            stopped = true;
            stoppedSince = now;
            return ArrivalDecision.Wait;
        }

        if (now - stoppedSince < SettleMs)
        {
            return ArrivalDecision.Wait;
        }

        if (Rewalks >= MaxRewalks)
        {
            GaveUp = true;
            return ArrivalDecision.GiveUp;
        }

        Rewalks++;
        stopped = false;
        return ArrivalDecision.Rewalk;
    }
}
