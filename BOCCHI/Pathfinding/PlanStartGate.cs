using BOCCHI.Chains;

namespace BOCCHI.Pathfinding;

public enum PlanStartState : byte
{
    /// <summary>可以取角色位置當起點規劃。</summary>
    Ready,

    /// <summary>在詠唱。</summary>
    Casting,

    /// <summary>讀圖中（區域切換、傳送），或還讀不到角色。</summary>
    Loading,

    /// <summary>「返回」詠唱剛結束、還沒開始讀圖（之後會被傳回營地）。</summary>
    AfterReturn,
}

/// <summary>
/// 巡迴開始／續跑時，規劃前先等詠唱、讀圖結束再取起點：「返回」詠唱完會被傳回營地，在詠唱中取的起點就錯了。
/// 純邏輯、不碰遊戲、不配置（離線驗收直接呼叫）；只在 framework 執行緒用。
/// </summary>
public sealed class PlanStartGate
{
    /// <summary>「返回」詠唱結束後最多等這麼久看讀圖開始（實機約 0.8 秒；返回動作鏈等讀圖開始的上限是 5 秒）。</summary>
    public const long LoadingStartMs = 3_000;

    /// <summary>一次最多等這麼久，之後照常規劃。</summary>
    public const long MaxWaitMs = 20_000;

    private bool waiting;

    private long since;

    private bool returnPending;

    private bool returnEnded;

    private long returnEndedAt;

    public bool IsWaiting
    {
        get => waiting;
    }

    public void Reset()
    {
        waiting = false;
        returnPending = false;
        returnEnded = false;
    }

    /// <param name="cast">現在的詠唱（<see cref="ReturnCast.Classify"/>）。</param>
    /// <param name="playerAvailable">讀得到角色。</param>
    /// <param name="betweenAreas">BetweenAreas 或 BetweenAreas51。</param>
    /// <param name="timedOut">等滿 <see cref="MaxWaitMs"/> 才放行。</param>
    /// <param name="waitedMs">放行時總共等了多久（沒等過是 0）。</param>
    /// <returns>Ready＝現在規劃；其他＝這一拍先不規劃。</returns>
    public PlanStartState Step(long now, ReturnCastKind cast, bool playerAvailable, bool betweenAreas, out bool timedOut, out long waitedMs)
    {
        timedOut = false;
        waitedMs = 0;
        var state = Classify(now, cast, playerAvailable, betweenAreas);
        if (state == PlanStartState.Ready)
        {
            waitedMs = waiting ? now - since : 0;
            Reset();
            return PlanStartState.Ready;
        }

        if (!waiting)
        {
            waiting = true;
            since = now;
        }

        if (now - since < MaxWaitMs)
        {
            return state;
        }

        timedOut = true;
        waitedMs = now - since;
        Reset();
        return PlanStartState.Ready;
    }

    private PlanStartState Classify(long now, ReturnCastKind cast, bool playerAvailable, bool betweenAreas)
    {
        if (betweenAreas || !playerAvailable)
        {
            returnPending = false;
            returnEnded = false;
            return PlanStartState.Loading;
        }

        if (cast != ReturnCastKind.None)
        {
            if (cast == ReturnCastKind.Return)
            {
                returnPending = true;
                returnEnded = false;
            }

            return PlanStartState.Casting;
        }

        if (!returnPending)
        {
            return PlanStartState.Ready;
        }

        if (!returnEnded)
        {
            returnEnded = true;
            returnEndedAt = now;
        }

        if (now - returnEndedAt < LoadingStartMs)
        {
            return PlanStartState.AfterReturn;
        }

        returnPending = false;
        returnEnded = false;
        return PlanStartState.Ready;
    }
}
