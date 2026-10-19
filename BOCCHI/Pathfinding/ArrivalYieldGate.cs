using System.Numerics;

namespace BOCCHI.Pathfinding;

public enum ArrivalYieldDecision
{
    /// <summary>照 <see cref="ArrivalGuard"/> 判斷（沒在讓位）。</summary>
    Arrival,

    /// <summary>讓位中或角色自己在動：不算停下、不重走。</summary>
    Hold,

    /// <summary>讓位結束、角色已靜止：從目前位置重送一次，不算重走次數。</summary>
    Resume,
}

/// <summary>
/// ChainHelper.PathfindToAndWait 走路段的讓位（非法模式順路開白銀寶箱、走去城內乙太之晶）：判準同趕路讓位（<see cref="ManualMoveDetector"/>，含短按與互動）。
/// 一次讓位上限、一趟的按鍵／互動額度都沿用它；到上限後這一段照 <see cref="ArrivalGuard"/>。
/// <see cref="Enabled"/> 為 false 時一律 <see cref="ArrivalYieldDecision.Arrival"/>（與沒有讓位時逐拍相同）。純邏輯、不碰遊戲；只在 framework 執行緒用。
/// </summary>
public sealed class ArrivalYieldGate
{
    public ManualMoveDetector Manual { get; } = new();

    /// <summary>每一拍由呼叫端照設定填；關閉時讓位狀態清空。</summary>
    public bool Enabled { get; set; }

    public bool ShouldSample(long now)
    {
        return Enabled && Manual.ShouldSample(now);
    }

    /// <param name="busy">上下坐騎、詠唱、跳躍、讀圖中。</param>
    /// <param name="touched">使用者剛按過移動鍵或在不是 BOCCHI 自己發起的互動事件裡。</param>
    public TravelYieldStep Observe(Vector3 player, bool navigating, bool busy, bool touched, long now)
    {
        if (!Enabled)
        {
            Manual.Reset();
            return default;
        }

        return Manual.Observe(player, navigating, busy, false, touched, now);
    }

    public ArrivalYieldDecision Next(bool navigating)
    {
        if (!Enabled || Manual.Expired)
        {
            return ArrivalYieldDecision.Arrival;
        }

        if (!navigating && Manual.TakeResume())
        {
            return ArrivalYieldDecision.Resume;
        }

        return Manual.Yielding || Manual.MovingNow ? ArrivalYieldDecision.Hold : ArrivalYieldDecision.Arrival;
    }
}
