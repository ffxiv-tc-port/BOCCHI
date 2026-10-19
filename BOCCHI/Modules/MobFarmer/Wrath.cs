using System;
using System.Collections.Generic;
using BOCCHI.Automation;
using BOCCHI.Data;
using BOCCHI.Enums;
using ECommons.DalamudServices;
using Ocelot.IPC;
using Ocelot.Modules;

namespace BOCCHI.Modules.MobFarmer;

// 租約只在真的要改 WrathCombo 設定時才取得，停止打怪就交還：租約在手的期間，被改過的選項蓋住使用者自己的設定。
// 本檔契約：只在 framework 執行緒呼叫（狀態機、Farmer.Update 每幀的 Tick、停止與卸載）。
// WrathCombo 收回租約（換職業、重新載入）後，Tick 重新取得並補回最後一次要的開關；
// 你在 WrathCombo 撤銷控制、或這一輪被收回太多次，就停手到下一次開始打怪。
public class Wrath : IRotationPlugin
{
    private const long RetryIntervalMs = 5000;

    private readonly WrathPhantomIpc wrath;

    private readonly string leaseOwner;

    private readonly WrathLeaseCallback callback;

    private Guid? lease;

    // 最後一次要 WrathCombo 設成的選項與值；租約被收回後照這個補回。
    private (string Option, bool Enabled)? desired;

    private bool retakePending;

    private string? retakeNote;

    private int retakes;

    private bool gaveUp;

    private long nextRetryAt;

    // 重新取得失敗只記第一次，之後照間隔安靜重試。
    private bool retryReported;

    private Dictionary<JobId, string> WrathOptions = new()
    {
        { JobId.Cannoneer, "Phantom_Cannoneer" },
    };

    public Wrath(IModule module)
    {
        wrath = module.GetIPCSubscriber<WrathPhantomIpc>();
        leaseOwner = module.GetType().FullName!;
        callback = new WrathLeaseCallback("MobFarmer");
    }

    public void PhantomJobOn(Job? job = null)
    {
        SetPhantomOption(job, true);
    }

    public void PhantomJobOff(Job? job = null)
    {
        SetPhantomOption(job, false);
    }

    public void Tick()
    {
        HandleRevoked();
        if (!retakePending || gaveUp || desired is not { } want)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now < nextRetryAt)
        {
            return;
        }

        nextRetryAt = now + RetryIntervalMs;
        if (!Apply(want.Option, want.Enabled, true))
        {
            return;
        }

        retakePending = false;
        retryReported = false;
        Svc.Log.Information($"[BOCCHI][自動打怪] 已重新取得 WrathCombo 的租約，{want.Option} 補回{(want.Enabled ? "開啟" : "關閉")}（前一份被收回：{retakeNote}）。");
        retakeNote = null;
    }

    public void Release()
    {
        desired = null;
        retakePending = false;
        retakeNote = null;
        retakes = 0;
        gaveUp = false;
        nextRetryAt = 0;
        retryReported = false;
        callback.Take();

        if (lease is not { } id)
        {
            return;
        }

        lease = null;

        // 插件卸載順序不保證，WrathCombo 可能已經不在（IpcNotReadyError）；對不存在的插件交還不了，記下來繼續。
        try
        {
            wrath.ReleaseControl(id);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[Wrath] ReleaseControl failed (WrathCombo most likely already unloaded): {ex.Message}");
        }
    }

    private void SetPhantomOption(Job? job, bool enabled)
    {
        job ??= Job.Current;
        if (job == null)
        {
            return;
        }

        if (!WrathOptions.TryGetValue(job.id, out var option))
        {
            return;
        }

        HandleRevoked();
        desired = (option, enabled);
        if (gaveUp)
        {
            return;
        }

        // 正在等重新取得：交給 Tick 下一幀照新的值補，不在這裡另外搶。
        if (retakePending)
        {
            nextRetryAt = 0;
            return;
        }

        Apply(option, enabled, false);
    }

    // 回傳是否設成。租約失效時在本地放手，由 Lost 決定要不要重新取得。
    private bool Apply(string option, bool enabled, bool retaking)
    {
        if (!TryGetLease(out var id, retaking))
        {
            return false;
        }

        WrathCombo.SetResult result;
        try
        {
            result = wrath.SetComboOptionState(id, option, enabled);
        }
        catch (Exception ex)
        {
            if (ShouldReport(retaking))
            {
                Svc.Log.Information($"[BOCCHI][自動打怪] 切換 WrathCombo 的 {option} 失敗（WrathCombo 可能剛好卸載），這次略過：{ex.Message}");
            }

            return false;
        }

        switch (result)
        {
            case WrathCombo.SetResult.Okay:
            case WrathCombo.SetResult.OkayWorking:
            case WrathCombo.SetResult.Duplicate:
                return true;
            case WrathCombo.SetResult.InvalidLease:
                callback.Take();
                Lost("WrathCombo 表示這份租約已經失效", true, retaking ? RetryIntervalMs : 0);
                return false;
            case WrathCombo.SetResult.BlacklistedLease:
                callback.Take();
                Lost("你最近在 WrathCombo 撤銷過 BOCCHI 的控制", false, 0);
                return false;
            default:
                if (ShouldReport(retaking))
                {
                    Svc.Log.Information($"[BOCCHI][自動打怪] WrathCombo 沒有套用 {option}（{result}），這次略過。");
                }

                return false;
        }
    }

    private bool TryGetLease(out Guid id, bool retaking)
    {
        if (lease is { } existing)
        {
            id = existing;
            return true;
        }

        id = default;
        Guid? created;
        try
        {
            created = wrath.RegisterForLeaseWithCallback(Svc.PluginInterface.InternalName, leaseOwner, callback.Prefix);
        }
        catch (Exception ex)
        {
            if (ShouldReport(retaking))
            {
                var next = retaking ? "之後每 5 秒安靜重試" : "這次不切換輔助砲擊士的連段設定";
                Svc.Log.Information($"[BOCCHI][自動打怪] 向 WrathCombo 取得租約失敗（WrathCombo 可能沒有載入或正在重新載入），{next}：{ex.Message}");
            }

            return false;
        }

        if (created is not { } got)
        {
            if (retaking)
            {
                GiveUp("重新取得時 WrathCombo 拒絕租約（你最近在 WrathCombo 撤銷過 BOCCHI 的控制）");
            }
            else
            {
                Svc.Log.Information("[BOCCHI][自動打怪] WrathCombo 拒絕租約（可能被你暫時封鎖了），這次不切換輔助砲擊士的連段設定。");
            }

            return false;
        }

        lease = got;
        id = got;
        return true;
    }

    // 被收回的租約在 WrathCombo 那邊已經不在：只在本地放手，不呼叫 ReleaseControl。
    private void HandleRevoked()
    {
        if (callback.Take() is not { } notice || lease == null)
        {
            return;
        }

        Lost(notice.Describe(), notice.MayRetake, notice.WrathUnloading ? RetryIntervalMs : 0);
    }

    private void Lost(string why, bool mayRetake, long delayMs)
    {
        lease = null;
        if (!mayRetake)
        {
            GiveUp($"WrathCombo 收回了自動打怪的租約（{why}）");
            return;
        }

        if (++retakes > WrathLeaseCallback.MaxRetakesPerRun)
        {
            GiveUp($"WrathCombo 收回了自動打怪的租約（{why}），這一輪已被收回超過 {WrathLeaseCallback.MaxRetakesPerRun} 次");
            return;
        }

        retakePending = true;
        retakeNote = why;
        retryReported = false;
        nextRetryAt = Environment.TickCount64 + delayMs;
        var when = delayMs > 0 ? $"{delayMs / 1000} 秒後（之後每 5 秒）" : "馬上";
        Svc.Log.Information($"[BOCCHI][自動打怪] WrathCombo 收回了自動打怪的租約（{why}），{when}重新取得並補回連段設定。");
    }

    private void GiveUp(string what)
    {
        gaveUp = true;
        retakePending = false;
        Svc.Log.Information($"[BOCCHI][自動打怪] {what}；這一輪不再切換 WrathCombo 的連段設定（照你自己的設定），停止再開始自動打怪後才會重新取得。");
    }

    private bool ShouldReport(bool retaking)
    {
        if (!retaking)
        {
            return true;
        }

        if (retryReported)
        {
            return false;
        }

        retryReported = true;
        return true;
    }

    void IDisposable.Dispose()
    {
        Release();
        callback.Dispose();
    }
}
