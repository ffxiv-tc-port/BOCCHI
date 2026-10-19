using System;
using System.Threading;
using Dalamud.Plugin.Ipc;
using ECommons.DalamudServices;

namespace BOCCHI.Automation;

// 本檔契約：WrathCombo 收回租約時，在收回它的那條執行緒上同步呼叫 OnCancelled（不保證是 framework 執行緒），這裡只記下原因就返回；
// 持有租約的一方在 framework 執行緒用 Take() 取走，重新註冊與重新套用都在那裡做。
// 回呼不帶租約 ID，所以每個租約持有者各用一個通道名。
public sealed class WrathLeaseCallback : IDisposable
{
    /// <summary>一輪（一次開始到停止）內最多重新取得幾次；超過就當成和 WrathCombo 互搶，停手到下一輪。</summary>
    public const int MaxRetakesPerRun = 10;

    // 數值對照 WrathCombo 的 CancellationReason。
    private const int WrathUserManuallyCancelled = 0;

    private const int LeaseePluginDisabled = 1;

    private const int WrathPluginDisabled = 2;

    private const int LeaseeReleased = 3;

    private const int AllServicesSuspended = 4;

    private const int JobChanged = 5;

    private readonly ICallGateProvider<int, string, object> provider;

    private Notice? pending;

    public WrathLeaseCallback(string channel)
    {
        Prefix = $"{Svc.PluginInterface.InternalName}.{channel}";
        provider = Svc.PluginInterface.GetIpcProvider<int, string, object>($"{Prefix}.WrathComboCallback");
        provider.RegisterAction(OnCancelled);
    }

    /// <summary>傳給 WrathCombo <c>RegisterForLeaseWithCallback</c> 的前綴；它收回租約時呼叫「前綴.WrathComboCallback」。</summary>
    public string Prefix { get; }

    /// <summary>取走最近一次收回通知，沒有時回 null。只在 framework 執行緒呼叫。</summary>
    public Notice? Take()
    {
        return Interlocked.Exchange(ref pending, null);
    }

    public void Dispose()
    {
        provider.UnregisterAction();
    }

    private void OnCancelled(int reason, string info)
    {
        // 自己呼叫 ReleaseControl 也會收到一次，那不是被收回。
        if (reason == LeaseeReleased)
        {
            return;
        }

        Interlocked.Exchange(ref pending, new Notice(reason, info ?? string.Empty));
    }

    public sealed record Notice(int Reason, string Info)
    {
        /// <summary>WrathCombo 自己的狀態變了（換職業、重新登入、重新載入）才重新取得；你撤銷或暫停外部控制就尊重，不再取。</summary>
        public bool MayRetake
        {
            get => Reason is LeaseePluginDisabled or WrathPluginDisabled or JobChanged;
        }

        /// <summary>WrathCombo 正在卸載：不要馬上重取，照平常的間隔等它重新載入。</summary>
        public bool WrathUnloading
        {
            get => Reason == WrathPluginDisabled;
        }

        public string Describe()
        {
            var text = Reason switch
            {
                WrathUserManuallyCancelled => "你在 WrathCombo 撤銷了 BOCCHI 的控制",
                LeaseePluginDisabled => "WrathCombo 判斷 BOCCHI 已停用",
                WrathPluginDisabled => "WrathCombo 正在卸載或重新載入",
                AllServicesSuspended => "WrathCombo 暫停了所有插件的控制",
                JobChanged => "換職業或重新登入後，WrathCombo 會收回所有插件的控制",
                _ => $"WrathCombo 回報的原因代碼 {Reason}",
            };

            return string.IsNullOrWhiteSpace(Info) ? text : $"{text}；{Info.Trim()}";
        }
    }
}
