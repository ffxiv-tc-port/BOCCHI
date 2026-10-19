using ECommons.EzIpcManager;

namespace BOCCHI.Automation;

/// <summary>對外的停止端點：<c>BOCCHI.StopAll</c>（Action&lt;string&gt;，參數是呼叫端署名，寫進記錄）。</summary>
/// <remarks>
/// 呼叫端的執行緒不固定：在 framework 執行緒上當場停完；其他執行緒只排隊，下一幀才停。
/// 刻意只提供停止、不提供開始——開始一律要使用者自己按。
/// </remarks>
public sealed class AutomationIpc
{
    private readonly AutomationControl control;

    public AutomationIpc(AutomationControl control)
    {
        this.control = control;
        EzIPC.Init(this, "BOCCHI");
    }

    [EzIPC("StopAll")]
    private void StopAll(string source)
    {
        control.RequestStopAll(source);
    }
}
