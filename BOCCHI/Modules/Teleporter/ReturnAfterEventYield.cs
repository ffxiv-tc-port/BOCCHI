using BOCCHI.Automation;

namespace BOCCHI.Modules.Teleporter;

/// <summary>「打完自動返回」要讓路給哪幾項自動化。純邏輯、不碰遊戲（離線驗收直接呼叫）。</summary>
public static class ReturnAfterEventYield
{
    /// <returns>true＝這一項在跑時不送返回。非法模式不在這裡：它開著時另有判斷（打完返回整個不動作）。</returns>
    public static bool YieldsTo(AutomationKind kind)
    {
        return kind != AutomationKind.IllegalMode && kind.DrivesMovement();
    }
}
