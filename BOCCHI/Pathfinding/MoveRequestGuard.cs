using System.Numerics;

namespace BOCCHI.Pathfinding;

public enum MoveRequestDecision
{
    /// <summary>這一拍送出 PathfindAndMoveTo。</summary>
    Send,

    /// <summary>不送：正在走、還在算路、導航網格還沒好，或離上一次重送不到 <see cref="MoveRequestGuard.RetryIntervalMs"/>。</summary>
    Wait,

    /// <summary>同一個目的地超過 <see cref="MoveRequestGuard.NoStartLimitMs"/> 都沒開始走；換目的地或 Reset 之前一直回這個。</summary>
    GiveUp,
}

/// <summary>
/// 決定要不要對 vnavmesh 重送同一個目的地的移動請求。純邏輯、不碰遊戲、不配置；只在 framework 執行緒用。
/// vnavmesh 算路期間 Path.IsRunning 是 false，「沒在走就重送」會讓每次重送蓋掉還沒算完的那一次：
/// 算路比重送間隔慢時角色永遠起不了步。所以「在走」與「在算」都不送，算失敗才隔一段時間重送。
/// </summary>
public sealed class MoveRequestGuard
{
    public const long NoStartLimitMs = 20_000;

    public const long RetryIntervalMs = 1_000;

    // 每拍重算的同一個目的地會有浮點誤差，差這麼多以內當成同一個。
    public const float SameDestinationM = 0.5f;

    // 兩拍之間就走完的短路看不到 IsRunning；角色離計時起點超過這麼遠也算開始走了。
    public const float ProgressM = 1f;

    private bool active;

    private Vector3 destination;

    private Vector3 anchor;

    private long windowStart;

    private long lastSendAt;

    public bool GaveUp { get; private set; }

    /// <summary>目前這段計時裡送了幾次（開始走、有進展或換目的地就歸零）。</summary>
    public int Sends { get; private set; }

    /// <summary>目前這段計時從什麼時候開始（<see cref="Next"/> 的 now 同一個時鐘）。</summary>
    public long WindowStart
    {
        get => windowStart;
    }

    public void Reset()
    {
        active = false;
        GaveUp = false;
        Sends = 0;
    }

    /// <param name="moving">vnavmesh Path.IsRunning。</param>
    /// <param name="computing">vnavmesh SimpleMove.PathfindInProgress（算路中或還有排隊的請求）。</param>
    /// <param name="meshReady">vnavmesh Nav.IsReady；沒好時不送也不計時（建網格可能要好幾分鐘）。</param>
    public MoveRequestDecision Next(Vector3 target, Vector3 player, bool moving, bool computing, bool meshReady, long now)
    {
        if (!active || Vector3.DistanceSquared(target, destination) > SameDestinationM * SameDestinationM)
        {
            active = true;
            GaveUp = false;
            destination = target;
            Restart(player, now);
        }

        if (GaveUp)
        {
            return MoveRequestDecision.GiveUp;
        }

        if (moving || !meshReady || Vector3.DistanceSquared(player, anchor) > ProgressM * ProgressM)
        {
            Restart(player, now);
            if (moving || !meshReady)
            {
                return MoveRequestDecision.Wait;
            }
        }

        if (now - windowStart > NoStartLimitMs)
        {
            GaveUp = true;
            return MoveRequestDecision.GiveUp;
        }

        if (computing || (Sends > 0 && now - lastSendAt < RetryIntervalMs))
        {
            return MoveRequestDecision.Wait;
        }

        Sends++;
        lastSendAt = now;
        return MoveRequestDecision.Send;
    }

    private void Restart(Vector3 player, long now)
    {
        anchor = player;
        windowStart = now;
        Sends = 0;
    }
}
