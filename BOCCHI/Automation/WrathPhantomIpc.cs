using System;
using System.Collections.Generic;
using ECommons.EzIpcManager;
using Ocelot.IPC;

namespace BOCCHI.Automation;

/// <summary>輔助職業技能清單與自動打怪用到的 WrathCombo 端點；名稱與型別對照我方 WrathCombo 的 Provider。</summary>
/// <remarks>SetResult 借用 Ocelot 那份：數值與 WrathCombo 的相同，回傳時由 Dalamud 以 JSON 轉換。</remarks>
public sealed class WrathPhantomIpc() : IPCSubscriber("WrathCombo")
{
    /// <summary>該職業的「根連段名 → 底下全部選項名」；WrathCombo 還沒初始化或太舊時可能擲例外或回 null。</summary>
    [EzIPC] public readonly Func<uint, Dictionary<string, List<string>>?> GetComboOptionNamesForJob = null!;

    /// <summary>選項實際生效的狀態：有租約控制時回租約的值，否則回使用者自己的設定。</summary>
    [EzIPC] public readonly Func<string, bool> GetComboOptionState = null!;

    [EzIPC] public readonly Func<string, string, Guid?> RegisterForLease = null!;

    /// <summary>第三個參數是回呼前綴：租約被收回時 WrathCombo 呼叫「前綴.WrathComboCallback」（int 原因, string 補充）。</summary>
    [EzIPC] public readonly Func<string, string, string?, Guid?> RegisterForLeaseWithCallback = null!;

    [EzIPC] public readonly Action<Guid> ReleaseControl = null!;

    [EzIPC] public readonly Func<Guid, string, bool, WrathCombo.SetResult> SetComboOptionState = null!;

    /// <summary>使用者自己存的輔助職業選項開關（選項名 → 開關），不受租約影響；設定還沒載入時回 null。</summary>
    /// <remarks>較舊的 WrathCombo 沒有這個端點，呼叫會擲例外。</remarks>
    [EzIPC] public readonly Func<Dictionary<string, bool>?> GetOccultCrescentUserOptionStates = null!;

    /// <summary>改使用者自己存的輔助職業選項開關並存檔（選項名 → 開關, 呼叫者名稱）。</summary>
    /// <returns>0 以上＝實際改變的項數；負值＝整批拒絕、什麼都沒改。</returns>
    /// <remarks>只在 framework 執行緒、使用者自己操作之後呼叫；這是在改另一個插件的設定檔。</remarks>
    [EzIPC] public readonly Func<Dictionary<string, bool>, string, int> SetOccultCrescentUserOptionStates = null!;
}
