using System.Numerics;

namespace BOCCHI.Pathfinding;

public enum TravelDecision
{
    /// <summary>還在動、剛停下，或讓位中。</summary>
    Wait,

    /// <summary>停下滿 <see cref="ArrivalGuard.SettleMs"/>：在原地重送同一個目的地（算進重送次數）。</summary>
    Rewalk,

    /// <summary>讓位結束、角色已靜止：從目前位置接續原目的地（不算進重送次數）。</summary>
    Resume,

    /// <summary>重送用完：呼叫端照舊中止整趟重來。</summary>
    GiveUp,
}

/// <summary>
/// 非法模式趕路監看看到「導航停了、還沒到」時的判斷。純邏輯、不碰遊戲、不配置；只在 framework 執行緒用。
/// 上下坐騎、詠唱、跳躍、讀圖中算還在動；停下滿 <see cref="ArrivalGuard.SettleMs"/> 才在原地重送同一個目的地，
/// 沒有淨前進 <see cref="ProgressM"/> 就最多重送 <see cref="ArrivalGuard.MaxRewalks"/> 次，之後回 GiveUp：
/// 呼叫端照舊中止整趟重來（真的卡住仍然看得到）。
/// <see cref="YieldEnabled"/> 時另外照 <see cref="ManualMoveDetector"/> 讓位：導航停著而角色自己在動就不算停下、不重送；
/// 讓位滿上限後這一趟持續移動回到上面的規則（按鍵／互動讓位照常）。關閉時與沒有讓位的版本逐拍相同。
/// </summary>
public sealed class TravelStopGuard
{
    /// <summary>離目的地比上一次計數起點近這麼多（米）才算有進展，重送次數歸零；所以歸零次數最多是全程距離／5。</summary>
    public const float ProgressM = 5f;

    private readonly ArrivalGuard arrival = new();

    private float anchor = float.NaN;

    public ManualMoveDetector Manual { get; } = new();

    private readonly StopSettle settle = new();

    /// <summary>每一拍由呼叫端照設定填；關閉時讓位狀態清空、不取樣。</summary>
    public bool YieldEnabled { get; set; }

    /// <summary>這一趟總共重送了幾次（不因進展歸零；讓位後的接續不算）。</summary>
    public int Rewalks { get; private set; }

    private long yieldCredit;

    /// <summary>讓位中。</summary>
    public bool Yielding
    {
        get => YieldEnabled && Manual.Yielding;
    }

    /// <summary>呼叫端自己的移動（追敵人、挑站位點）這一拍要不要先不送：讓位中、角色還在自己動、或導航剛停還沒靜止（<see cref="StopSettle"/>）。</summary>
    /// <param name="navigating">現在 vnavmesh 在走或還在算路。</param>
    public bool HoldsOwnMoves(bool navigating)
    {
        return YieldEnabled && !Manual.Expired && (Manual.Yielding || Manual.MovingNow || settle.Holding(navigating));
    }

    public void Reset()
    {
        arrival.Reset();
        anchor = float.NaN;
        Rewalks = 0;
        yieldCredit = 0;
        Manual.Reset();
        settle.Clear();
    }

    public bool ShouldSample(long now)
    {
        return YieldEnabled && Manual.ShouldSample(now);
    }

    /// <summary>呼叫端已經把操作交給使用者（返回讓位）：不必先看到手動移動，直接開始讓位。選項關著時不動作。</summary>
    public void BeginHandOver(long now)
    {
        if (YieldEnabled)
        {
            Manual.BeginYield(now);
        }
    }

    /// <summary>取樣一次（呼叫端照 <see cref="ShouldSample"/> 約每 100 毫秒一次，監看的每一條路徑都要走到）。</summary>
    public TravelYieldStep Observe(Vector3 player, bool navigating, bool busy, long now)
    {
        return Observe(player, navigating, busy, false, now);
    }

    /// <param name="external">Lifestream 正在執行任務（見 <see cref="ManualMoveDetector"/>）。</param>
    public TravelYieldStep Observe(Vector3 player, bool navigating, bool busy, bool external, long now)
    {
        return Observe(player, navigating, busy, external, false, now);
    }

    /// <param name="touched">使用者剛按過移動鍵或在互動事件裡（見 <see cref="ManualMoveDetector"/>）。</param>
    public TravelYieldStep Observe(Vector3 player, bool navigating, bool busy, bool external, bool touched, long now)
    {
        if (!YieldEnabled)
        {
            Manual.Reset();
            settle.Clear();
            return default;
        }

        var step = Manual.Observe(player, navigating, busy, external, touched, now);
        settle.Observe(navigating, busy, step.Speed, now);
        yieldCredit += step.YieldedMs;
        return step;
    }

    /// <summary>上次取走之後讓位（含讓給 Lifestream）的毫秒數，取走就清掉；趕路監看逾時那一刻補回時限用。</summary>
    public long TakeYieldCredit()
    {
        var credit = yieldCredit;
        yieldCredit = 0;
        return credit;
    }

    /// <param name="distanceLeft">角色到目的地的距離。</param>
    /// <param name="navigating">vnavmesh 在走或還在算路。</param>
    /// <param name="busy">上下坐騎、詠唱、跳躍、讀圖中。</param>
    public TravelDecision Next(float distanceLeft, bool navigating, bool busy, long now)
    {
        if (float.IsNaN(anchor))
        {
            anchor = distanceLeft;
        }
        else if (distanceLeft <= anchor - ProgressM)
        {
            arrival.ClearRewalks();
            anchor = distanceLeft;
        }

        if (YieldEnabled && !Manual.Expired)
        {
            // 讓位或角色還在自己動時不算停下：停下計時從角色真的停住才開始。
            if (!navigating && Manual.TakeResume())
            {
                arrival.Next(distanceLeft, -1f, true, now);
                return TravelDecision.Resume;
            }

            if (Manual.Yielding || Manual.MovingNow)
            {
                arrival.Next(distanceLeft, -1f, true, now);
                return TravelDecision.Wait;
            }
        }

        switch (arrival.Next(distanceLeft, -1f, navigating || busy, now))
        {
            case ArrivalDecision.Rewalk:
                Rewalks++;
                return TravelDecision.Rewalk;
            case ArrivalDecision.GiveUp:
                return TravelDecision.GiveUp;
            default:
                return TravelDecision.Wait;
        }
    }
}
