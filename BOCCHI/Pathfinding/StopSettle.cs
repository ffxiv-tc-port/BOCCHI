namespace BOCCHI.Pathfinding;

/// <summary>
/// 導航停下後先不送自己的移動：要等取樣看到停下、角色靜止（低於 <see cref="ManualMoveDetector.StillSpeedMax"/>）滿 <see cref="SettleMs"/>，
/// 最多等 <see cref="SettleLimitMs"/>。vnavmesh 因玩家輸入取消路徑後，下一拍常排在取樣之前，那時還看不出角色在動。
/// 巡迴讓位（<see cref="HuntYieldGate"/>）與非法模式趕路追敵人（<see cref="TravelStopGuard"/>）共用。純邏輯、不碰遊戲、不配置；只在 framework 執行緒用。
/// </summary>
public sealed class StopSettle
{
    /// <summary>停下後角色要靜止這麼久才放行，同非法模式趕路的停下計時（<see cref="ArrivalGuard.SettleMs"/>）。</summary>
    public const long SettleMs = 500;

    /// <summary>停下後最多等這麼久就放行（角色一直在 1～1.5 米／秒之間動、或一直在詠唱）。</summary>
    public const long SettleLimitMs = 3_000;

    // 上一次取樣時導航在走或在算路。
    private bool sampledNavigating;

    private bool settling;

    private long settleSince;

    private bool quiet;

    private long quietSince;

    /// <param name="navigating">現在 vnavmesh 在走或還在算路。</param>
    /// <returns>true＝這一拍先不送。</returns>
    public bool Holding(bool navigating)
    {
        return !navigating && (sampledNavigating || settling);
    }

    /// <summary>每次讓位取樣時呼叫（約 100 毫秒一次）；speed＝那次取樣的水平速度（-1＝不知道）。</summary>
    public void Observe(bool navigating, bool busy, float speed, long now)
    {
        if (navigating)
        {
            sampledNavigating = true;
            settling = false;
            return;
        }

        if (sampledNavigating)
        {
            sampledNavigating = false;
            settling = true;
            settleSince = now;
            quiet = false;
        }

        if (!settling)
        {
            return;
        }

        if (now - settleSince >= SettleLimitMs)
        {
            settling = false;
            return;
        }

        if (busy || speed < 0 || speed >= ManualMoveDetector.StillSpeedMax)
        {
            quiet = false;
            return;
        }

        if (!quiet)
        {
            quiet = true;
            quietSince = now;
        }

        if (now - quietSince >= SettleMs)
        {
            settling = false;
        }
    }

    public void Clear()
    {
        sampledNavigating = false;
        settling = false;
        quiet = false;
    }
}
