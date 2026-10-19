using System.Numerics;

namespace BOCCHI.Pathfinding;

public enum HuntMoveDecision
{
    /// <summary>照常交給 <see cref="MoveRequestGuard"/> 決定送不送。</summary>
    Send,

    /// <summary>角色在導航停著時自己移動（或已確認是手動、讓位中）：這一拍不送移動、不叫坐騎。</summary>
    Hold,

    /// <summary>讓位結束、角色已靜止：從目前位置接續，呼叫端先重設守衛再照常送。</summary>
    Resume,
}

/// <summary>
/// 巡迴走向點／乙太之晶時讓位給手動移動，判準與非法模式趕路讓位相同（<see cref="ManualMoveDetector"/>）。
/// 還沒確認是手動之前，角色在動也先不送（否則每次重送都把確認計時打斷）；讓位滿上限後持續移動回到照常重送（不等靜止），直到 <see cref="Reset"/>；按鍵／互動讓位照常。
/// 導航停下後照 <see cref="StopSettle"/> 等角色靜止才重送。呼叫端另給「剛按過移動鍵／在互動」時短按也讓位，額度每一步重算。
/// <see cref="Enabled"/> 為 false 時一律 Send，與沒有讓位的版本相同。純邏輯、不碰遊戲、不配置；只在 framework 執行緒用。
/// </summary>
public sealed class HuntYieldGate
{
    public ManualMoveDetector Manual { get; } = new();

    private readonly StopSettle settle = new();

    /// <summary>每一拍由呼叫端照設定填。</summary>
    public bool Enabled { get; set; }

    public bool Holding
    {
        get => Enabled && !Manual.Expired && (Manual.Yielding || Manual.MovingNow);
    }

    public void Reset()
    {
        Manual.Reset();
        settle.Clear();
    }

    /// <summary>換到下一步時呼叫：新目的地的第一次送出不等上一步的停下計時（讓位狀態照舊延續，按鍵／互動額度重算）。</summary>
    public void OnStepChanged()
    {
        Manual.ResetTouchBudget();
        settle.Clear();
    }

    public bool ShouldSample(long now)
    {
        return Enabled && Manual.ShouldSample(now);
    }

    /// <param name="navigating">vnavmesh 在走或還在算路。</param>
    /// <param name="busy">上下坐騎、詠唱、跳躍、讀圖中。</param>
    public TravelYieldStep Observe(Vector3 player, bool navigating, bool busy, long now)
    {
        return Observe(player, navigating, busy, false, now);
    }

    /// <param name="external">Lifestream 正在執行任務（見 <see cref="ManualMoveDetector"/>）。</param>
    public TravelYieldStep Observe(Vector3 player, bool navigating, bool busy, bool external, long now)
    {
        return Observe(player, navigating, busy, external, false, now);
    }

    /// <param name="touched">使用者剛按過移動鍵或在不是 BOCCHI 自己發起的互動事件裡（見 <see cref="ManualMoveDetector"/>）。</param>
    public TravelYieldStep Observe(Vector3 player, bool navigating, bool busy, bool external, bool touched, long now)
    {
        if (!Enabled)
        {
            Manual.Reset();
            settle.Clear();
            return default;
        }

        var step = Manual.Observe(player, navigating, busy, external, touched, now);
        settle.Observe(navigating, busy, step.Speed, now);
        return step;
    }

    public HuntMoveDecision Next(bool navigating)
    {
        if (!Enabled || Manual.Expired)
        {
            return HuntMoveDecision.Send;
        }

        if (!navigating && Manual.TakeResume())
        {
            return HuntMoveDecision.Resume;
        }

        if (Manual.Yielding || Manual.MovingNow)
        {
            return HuntMoveDecision.Hold;
        }

        return settle.Holding(navigating) ? HuntMoveDecision.Hold : HuntMoveDecision.Send;
    }
}
