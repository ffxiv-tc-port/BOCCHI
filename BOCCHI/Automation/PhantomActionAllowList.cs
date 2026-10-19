using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.DalamudServices;
using Ocelot;
using Ocelot.IPC;

namespace BOCCHI.Automation;

// 本檔契約：只在 framework 執行緒呼叫（AutomationControl 每幀 Sync、卸載時 Dispose、設定視窗讀狀態）。
// 總開關關著就完全不碰 WrathCombo：不取租約、不呼叫任何端點，行為與沒有這個功能時相同。
// 打開時，非法模式或自動打怪執行中取一份自己的租約：勾選的技能設開、沒勾的設關，輔助職業本身與中間選項跟著開關；
// 交還租約就恢復，因為 WrathCombo 的租約只蓋在使用者設定上面、不寫進設定檔。
// WrathCombo 收回租約（換職業、重新載入）時回呼只記原因，下一幀在這裡重新接管；你撤銷或這一輪被收回太多次就停手。
public sealed class PhantomActionAllowList : IDisposable
{
    private const long CheckIntervalMs = 5000;

    // 自動打怪自己會切換這個根選項（拉怪時開、戰鬥時關）；它執行時不去搶，只管底下的技能。
    private const string MobFarmerOwnedRoot = "Phantom_Cannoneer";

    private readonly Plugin plugin;

    private readonly WrathPhantomIpc? ipc;

    private Guid? lease;

    private (string Name, bool On)[] applied = [];

    private int allowedCount;

    private int blockedCount;

    private bool appliedForMobFarmer;

    private int revision;

    private int appliedRevision;

    // WrathCombo 這一輪拒絕過租約：停止、關掉總開關或改清單之前不再試。
    private bool gaveUp;

    private bool problemReported;

    private long nextCheckAt;

    // 第一次真的要取租約時才建立，總開關關著就不註冊任何東西。
    private WrathLeaseCallback? callback;

    private int retakes;

    // 前一份租約被收回的原因；重新接管成功時寫進那一行。
    private string? retakeNote;

    private readonly Action notifyChanged;

    public PhantomActionAllowList(Plugin plugin, WrathPhantomIpc? ipc)
    {
        this.plugin = plugin;
        this.ipc = ipc;
        Catalog = new WrathPhantomCatalog(ipc);
        OptionSync = new PhantomOptionSync(plugin, ipc, Catalog);
        notifyChanged = NotifyChanged;

        // 舊設定是語意相反的禁用清單：不自動反轉，只提醒一次。
        var legacy = plugin.Config.AutomatorConfig.TakeLegacyDisabledWrathOccultOptions();
        if (legacy != null)
        {
            Svc.Log.Information($"[BOCCHI][輔助職業技能] 上一版的「禁用清單」有 {legacy.Count} 項（{string.Join("、", legacy.Order(StringComparer.Ordinal))}）。新版改成「勾選＝可以使用」，語意相反，沒有自動轉換；需要的話請在非法模式設定打開總開關後重新勾選。舊清單下次存檔時移除。");
        }
    }

    public WrathPhantomCatalog Catalog { get; }

    /// <summary>清單與 WrathCombo 使用者設定的同步（同步開關關著時什麼都不做）；與租約是兩條路。</summary>
    public PhantomOptionSync OptionSync { get; }

    /// <summary>目前是否正拿著租約接管 WrathCombo 的輔助職業技能。</summary>
    public bool IsApplied
    {
        get => lease != null;
    }

    /// <summary>接管中設成可用的技能數（不含輔助職業本身與中間選項）。</summary>
    public int AllowedCount
    {
        get => allowedCount;
    }

    /// <summary>接管中設成關閉的技能數（不含輔助職業本身與中間選項）。</summary>
    public int BlockedCount
    {
        get => blockedCount;
    }

    /// <summary>上一幀非法模式或自動打怪是否在執行。</summary>
    public bool CombatRunning { get; private set; }

    /// <summary>設定視窗改了清單時呼叫；執行中的話下一幀改套新的清單。</summary>
    public void NotifyChanged()
    {
        revision++;
    }

    /// <param name="combatRunning">非法模式或自動打怪是否正在執行。</param>
    /// <param name="mobFarmerRunning">其中是不是自動打怪（它自己會切換輔助砲擊士的根選項）。</param>
    public void Sync(bool combatRunning, bool mobFarmerRunning)
    {
        CombatRunning = combatRunning;
        OptionSync.Tick(notifyChanged);
        var config = plugin.Config.AutomatorConfig;
        var wanted = combatRunning && config.ManageWrathOccultOptions && ipc != null;
        HandleRevoked(wanted);
        if (!wanted)
        {
            if (lease != null)
            {
                Release(combatRunning ? "總開關已關閉" : "自動化已停止");
            }

            gaveUp = false;
            problemReported = false;
            nextCheckAt = 0;
            appliedRevision = revision;
            retakes = 0;
            retakeNote = null;
            return;
        }

        if (revision != appliedRevision || (lease != null && mobFarmerRunning != appliedForMobFarmer))
        {
            appliedRevision = revision;
            gaveUp = false;
            problemReported = false;
            nextCheckAt = 0;
            retakes = 0;
            if (lease != null)
            {
                Release("清單變更，改套新的清單");
            }
        }

        var now = Environment.TickCount64;
        if (gaveUp || now < nextCheckAt)
        {
            return;
        }

        nextCheckAt = now + CheckIntervalMs;
        if (lease != null)
        {
            if (StillApplied())
            {
                return;
            }

            Release("WrathCombo 讀回來的開關和 BOCCHI 設的不同（租約已經不在，或被別的插件蓋過），重新套用");
        }

        Apply(config.AllowedWrathOccultOptions ?? [], mobFarmerRunning);
    }

    public void Dispose()
    {
        if (lease != null)
        {
            // 關遊戲時 WrathCombo 也在卸載，租約只存在它的記憶體裡、會跟著消失；卸載中不去呼叫別人的端點。
            if (Svc.Framework.IsFrameworkUnloading)
            {
                Forget();
            }
            else
            {
                Release("插件卸載");
            }
        }

        callback?.Dispose();
    }

    // 被收回的租約在 WrathCombo 那邊已經不在：只在本地放手，不呼叫 ReleaseControl。
    private void HandleRevoked(bool wanted)
    {
        if (callback?.Take() is not { } notice || lease == null)
        {
            return;
        }

        Forget();
        var why = notice.Describe();
        if (!wanted)
        {
            Svc.Log.Information($"[BOCCHI][輔助職業技能] WrathCombo 收回了輔助職業技能的控制（{why}）；自動化已停止，不再接管。");
            return;
        }

        if (!notice.MayRetake)
        {
            gaveUp = true;
            Svc.Log.Information($"[BOCCHI][輔助職業技能] WrathCombo 收回了輔助職業技能的控制（{why}），輔助職業技能恢復成你自己的設定；這一輪不再接管，停止再開始自動化或改了清單後才會重新接管。");
            return;
        }

        if (++retakes > WrathLeaseCallback.MaxRetakesPerRun)
        {
            gaveUp = true;
            Svc.Log.Information($"[BOCCHI][輔助職業技能] WrathCombo 收回了輔助職業技能的控制（{why}）；這一輪已被收回超過 {WrathLeaseCallback.MaxRetakesPerRun} 次，不再重新接管，停止再開始自動化或改了清單後才會重新接管。");
            return;
        }

        retakeNote = why;
        problemReported = false;
        nextCheckAt = notice.WrathUnloading ? Environment.TickCount64 + CheckIntervalMs : 0;
        var when = notice.WrathUnloading ? "等 WrathCombo 重新載入後（每 5 秒檢查一次）重新接管" : "馬上重新接管";
        Svc.Log.Information($"[BOCCHI][輔助職業技能] WrathCombo 收回了輔助職業技能的控制（{why}），{when}。");
    }

    private void Apply(HashSet<string> allowed, bool mobFarmerRunning)
    {
        if (!Catalog.Refresh())
        {
            ReportOnce("拿不到 WrathCombo 的輔助職業選項清單（WrathCombo 沒有載入或還沒準備好），先不接管，之後每 5 秒再試。");
            return;
        }

        var desired = BuildDesired(allowed, mobFarmerRunning);

        callback ??= new WrathLeaseCallback("PhantomActions");
        Guid? created;
        try
        {
            created = ipc!.RegisterForLeaseWithCallback(Svc.PluginInterface.InternalName, I18N.T("modules.automator.config.allowed_wrath_occult_options.lease_name"), callback.Prefix);
        }
        catch (Exception ex)
        {
            ReportOnce($"向 WrathCombo 取得租約失敗，之後每 5 秒再試：{ex.Message}");
            return;
        }

        if (created is not { } id)
        {
            gaveUp = true;
            ReportOnce("WrathCombo 拒絕租約（可能剛在 WrathCombo 撤銷過 BOCCHI 的控制，或它的 IPC 目前停用），這一輪不接管輔助職業技能。");
            return;
        }

        lease = id;
        appliedForMobFarmer = mobFarmerRunning;
        var done = new List<(string Name, bool On)>(desired.Count);
        var failed = new List<string>();
        WrathCombo.SetResult? lost = null;
        foreach (var (name, on) in desired)
        {
            WrathCombo.SetResult result;
            try
            {
                result = ipc.SetComboOptionState(id, name, on);
            }
            catch (Exception ex)
            {
                failed.Add($"{name}（{ex.GetType().Name}）");
                continue;
            }

            if (result is WrathCombo.SetResult.Okay or WrathCombo.SetResult.OkayWorking or WrathCombo.SetResult.Duplicate)
            {
                done.Add((name, on));
                continue;
            }

            failed.Add($"{name}（{result}）");
            if (result is WrathCombo.SetResult.InvalidLease or WrathCombo.SetResult.BlacklistedLease)
            {
                lost = result;
                break;
            }

            if (result is WrathCombo.SetResult.IPCDisabled)
            {
                break;
            }
        }

        if (lost != null)
        {
            // 套用到一半租約就沒了：WrathCombo 那邊已經收掉，只在本地放手；這次呼叫自己引發的收回通知一併丟掉。
            Forget();
            callback.Take();
            if (lost == WrathCombo.SetResult.BlacklistedLease)
            {
                gaveUp = true;
                ReportOnce("WrathCombo 在套用途中收回了租約（你最近在 WrathCombo 撤銷過 BOCCHI 的控制），這一輪不接管輔助職業技能。");
            }
            else
            {
                ReportOnce("WrathCombo 在套用途中表示租約已失效，之後每 5 秒再試。");
            }

            return;
        }

        applied = done.ToArray();
        var stale = allowed.Count(name => !Catalog.Contains(name));
        var notes = (failed.Count > 0 ? $"；沒設成的：{string.Join("、", failed)}" : string.Empty)
                    + (stale > 0 ? $"；清單裡 {stale} 項在目前的 WrathCombo 找不到，略過" : string.Empty);

        if (applied.Length == 0)
        {
            Release("一項都沒有設成");
            ReportOnce($"輔助職業技能一項都沒有設成功，之後每 5 秒再試{notes}");
            return;
        }

        var allowedLabels = done.Where(x => x.On && Catalog.Contains(x.Name)).Select(x => Catalog.LabelOf(x.Name)).ToList();
        allowedCount = allowedLabels.Count;
        blockedCount = done.Count(x => !x.On && Catalog.Contains(x.Name));
        problemReported = false;

        var farmerNote = mobFarmerRunning ? "（自動打怪執行中：輔助砲擊士本身的開關照舊由自動打怪切換）" : string.Empty;
        var allowedText = allowedLabels.Count > 0 ? string.Join("、", allowedLabels) : "無";
        var retakeText = retakeNote != null ? $"（重新接管；前一份被 WrathCombo 收回：{retakeNote}）" : string.Empty;
        retakeNote = null;
        Svc.Log.Information($"[BOCCHI][輔助職業技能] 已接管 WrathCombo 的輔助職業技能{retakeText}：可用 {allowedCount} 項（{allowedText}）、關閉 {blockedCount} 項{farmerNote}；停止自動化時恢復{notes}");
    }

    // 勾選的技能設開、沒勾的設關；輔助職業本身與中間選項只要底下有一項勾選就開，否則關（沒勾＝不用）。
    private List<(string Name, bool On)> BuildDesired(HashSet<string> allowed, bool mobFarmerRunning)
    {
        var desired = new List<(string Name, bool On)>();
        foreach (var group in Catalog.Groups)
        {
            var anyAllowed = false;
            foreach (var option in group.Options)
            {
                var on = allowed.Contains(option.Name);
                anyAllowed |= on;
                desired.Add((option.Name, on));
            }

            foreach (var gate in group.Gates)
            {
                desired.Add((gate.Name, gate.Children.Any(allowed.Contains)));
            }

            if (!(mobFarmerRunning && group.Root == MobFarmerOwnedRoot))
            {
                desired.Add((group.Root, anyAllowed));
            }
        }

        return desired;
    }

    // 任一項讀回來和設的不同＝這份租約已經不在，或被更新的租約蓋過。
    private bool StillApplied()
    {
        foreach (var (name, on) in applied)
        {
            try
            {
                if (ipc!.GetComboOptionState(name) != on)
                {
                    return false;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        return true;
    }

    private void Release(string reason)
    {
        if (lease is not { } id)
        {
            return;
        }

        var count = applied.Length;
        Forget();

        try
        {
            ipc?.ReleaseControl(id);
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[BOCCHI][輔助職業技能] 交還 WrathCombo 的租約失敗（WrathCombo 多半已經卸載，租約會跟著消失）：{ex.Message}");
        }

        if (count > 0)
        {
            Svc.Log.Information($"[BOCCHI][輔助職業技能] 已交還 WrathCombo 的控制（{reason}），輔助職業技能恢復成你自己的設定。");
        }
    }

    private void Forget()
    {
        lease = null;
        applied = [];
        allowedCount = 0;
        blockedCount = 0;
    }

    private void ReportOnce(string message)
    {
        if (problemReported)
        {
            return;
        }

        problemReported = true;
        Svc.Log.Information($"[BOCCHI][輔助職業技能] {message}");
    }
}
