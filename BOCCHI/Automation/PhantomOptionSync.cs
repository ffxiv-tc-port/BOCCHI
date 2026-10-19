using System;
using System.Collections.Generic;
using ECommons.DalamudServices;

namespace BOCCHI.Automation;

// 本檔契約：只在 framework 執行緒呼叫（PhantomActionAllowList.Sync 每幀 Tick；設定視窗繪製時讀狀態、送出要求）。
// 寫 WrathCombo 的使用者設定只發生在使用者自己的操作之後（清單勾選、全部勾選／取消、「以清單為準」），下一幀送出。
// 輪詢只讀：WrathCombo 那邊某一項的實際效果和上次看到的不同，才把 BOCCHI 自己的清單改成一樣。
// 同步剛打開時兩邊原本就不同的項目刻意不動，留在清單上標出來等使用者選一邊。
public sealed class PhantomOptionSync(Plugin plugin, WrathPhantomIpc? ipc, WrathPhantomCatalog catalog)
{
    public enum Status
    {
        Off,
        Reading,
        Ready,
        Unavailable,
    }

    private const long PollIntervalMs = 2000;

    private const long RetryIntervalMs = 5000;

    // 每個技能上次看到的 WrathCombo 實際效果；和這次不同＝WrathCombo 那邊改了。
    private readonly Dictionary<string, bool> seen = new(StringComparer.Ordinal);

    private readonly Dictionary<string, bool> effective = new(StringComparer.Ordinal);

    private readonly HashSet<string> pendingPush = new(StringComparer.Ordinal);

    private bool pendingImport;

    private long nextPollAt;

    private bool unavailableReported;

    public Status State { get; private set; } = Status.Off;

    /// <summary>有下一幀才處理的要求；這一幀的比對結果還沒反映它。</summary>
    public bool HasPending
    {
        get => pendingPush.Count > 0 || pendingImport;
    }

    /// <summary>WrathCombo 自己的設定下這個技能會不會被用；null＝不知道。</summary>
    public bool? WrathValue(string option)
    {
        return effective.TryGetValue(option, out var on) ? on : null;
    }

    /// <summary>使用者剛在清單改了這一項：下一幀寫進 WrathCombo 自己的設定。</summary>
    public void RequestPush(string option)
    {
        pendingPush.Add(option);
    }

    /// <summary>使用者要整份清單為準：下一幀把全部技能照清單寫進 WrathCombo 自己的設定。</summary>
    public void RequestPushAll(IReadOnlyList<WrathPhantomCatalog.Group> groups)
    {
        for (var g = 0; g < groups.Count; g++)
        {
            var options = groups[g].Options;
            for (var i = 0; i < options.Count; i++)
            {
                pendingPush.Add(options[i].Name);
            }
        }
    }

    /// <summary>使用者要以 WrathCombo 為準：下一幀把清單改成和 WrathCombo 自己的設定一樣（不寫 WrathCombo）。</summary>
    public void RequestImport()
    {
        pendingImport = true;
    }

    /// <param name="listChanged">清單在這裡被改動之後呼叫（執行中的租約要改套）。</param>
    public void Tick(Action listChanged)
    {
        var config = plugin.Config.AutomatorConfig;
        if (ipc == null || !config.ManageWrathOccultOptions || !config.SyncWrathOccultOptions)
        {
            if (State != Status.Off)
            {
                Reset();
            }

            return;
        }

        if (State == Status.Off)
        {
            State = Status.Reading;
        }

        var now = Environment.TickCount64;
        if (!HasPending && now < nextPollAt)
        {
            return;
        }

        nextPollAt = now + PollIntervalMs;
        var groups = catalog.GetGroups();
        var user = groups.Count > 0 ? ReadUser() : null;
        if (user == null)
        {
            GiveUpThisRound(now);
            return;
        }

        unavailableReported = false;
        var allowed = config.AllowedWrathOccultOptions ??= new HashSet<string>(StringComparer.Ordinal);
        var followed = Follow(groups, allowed, user);
        if (pendingPush.Count > 0 && !pendingImport)
        {
            Push(groups, allowed, user);
        }

        effective.Clear();
        for (var g = 0; g < groups.Count; g++)
        {
            var options = groups[g].Options;
            for (var i = 0; i < options.Count; i++)
            {
                if (PhantomSyncPlan.Effective(user, groups[g], options[i].Name) is { } on)
                {
                    effective[options[i].Name] = on;
                    seen[options[i].Name] = on;
                }
            }
        }

        var imported = pendingImport;
        pendingPush.Clear();
        pendingImport = false;
        State = Status.Ready;
        if (followed == null)
        {
            return;
        }

        plugin.Config.Save();
        listChanged();
        var why = imported ? "你按了「以 WrathCombo 為準」" : "WrathCombo 那邊改了";
        Svc.Log.Information($"[BOCCHI][輔助職業技能] 清單跟著 WrathCombo 自己的設定改了（{why}）：{followed}");
    }

    // 清單跟著 WrathCombo 改：全部（使用者要求以 WrathCombo 為準）或只有和上次看到不同的項目。回 null＝清單沒有變。
    private string? Follow(IReadOnlyList<WrathPhantomCatalog.Group> groups, HashSet<string> allowed, Dictionary<string, bool> user)
    {
        List<string>? changes = null;
        for (var g = 0; g < groups.Count; g++)
        {
            var options = groups[g].Options;
            for (var i = 0; i < options.Count; i++)
            {
                var name = options[i].Name;
                if (PhantomSyncPlan.Effective(user, groups[g], name) is not { } on || allowed.Contains(name) == on)
                {
                    continue;
                }

                var changedThere = seen.TryGetValue(name, out var before) && before != on && !pendingPush.Contains(name);
                if (!pendingImport && !changedThere)
                {
                    continue;
                }

                _ = on ? allowed.Add(name) : allowed.Remove(name);
                (changes ??= []).Add($"{options[i].Label}＝{(on ? "勾選" : "取消")}");
            }
        }

        return changes == null ? null : string.Join("、", changes);
    }

    private void Push(IReadOnlyList<WrathPhantomCatalog.Group> groups, HashSet<string> allowed, Dictionary<string, bool> user)
    {
        var writes = new Dictionary<string, bool>(StringComparer.Ordinal);
        var skipped = 0;
        for (var g = 0; g < groups.Count; g++)
        {
            if (!PhantomSyncPlan.AddWrites(groups[g], allowed, pendingPush, user, writes))
            {
                skipped++;
            }
        }

        var skippedNote = skipped > 0 ? $"；{skipped} 個輔助職業 WrathCombo 沒有回報完整的開關，沒有寫" : string.Empty;
        if (writes.Count == 0)
        {
            if (skipped > 0)
            {
                Svc.Log.Information($"[BOCCHI][輔助職業技能] 沒有寫進 WrathCombo 自己的設定{skippedNote}。");
            }

            return;
        }

        string failure;
        try
        {
            var result = ipc!.SetOccultCrescentUserOptionStates(writes, "BOCCHI");
            failure = result >= 0 ? string.Empty : result == -1 ? "WrathCombo 正在卸載" : $"WrathCombo 不接受這批選項（代碼 {result}）";
        }
        catch (Exception ex)
        {
            failure = $"WrathCombo 沒有載入，或版本太舊沒有這個功能（{ex.GetType().Name}）";
        }

        if (failure.Length > 0)
        {
            Svc.Log.Information($"[BOCCHI][輔助職業技能] 清單的改動沒有寫進 WrathCombo 自己的設定：{failure}。清單上會標出兩邊不同的項目。");
            return;
        }

        var parts = new List<string>(writes.Count);
        foreach (var (name, on) in writes)
        {
            user[name] = on;
            parts.Add($"{name}＝{(on ? "開" : "關")}");
        }

        Svc.Log.Information($"[BOCCHI][輔助職業技能] 已把清單的改動寫進 WrathCombo 自己的設定（{writes.Count} 個開關：{string.Join("、", parts)}）{skippedNote}。");
    }

    private Dictionary<string, bool>? ReadUser()
    {
        try
        {
            return ipc!.GetOccultCrescentUserOptionStates();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void GiveUpThisRound(long now)
    {
        var dropped = HasPending;
        pendingPush.Clear();
        pendingImport = false;
        effective.Clear();
        State = Status.Unavailable;
        nextPollAt = now + RetryIntervalMs;
        if (unavailableReported && !dropped)
        {
            return;
        }

        unavailableReported = true;
        var droppedNote = dropped ? "你剛才在清單的操作沒有同步過去；" : string.Empty;
        Svc.Log.Information($"[BOCCHI][輔助職業技能] 讀不到 WrathCombo 自己的輔助職業技能開關（WrathCombo 沒有載入，或版本太舊沒有提供）：{droppedNote}這段時間不同步，每 5 秒再試。");
    }

    private void Reset()
    {
        seen.Clear();
        effective.Clear();
        pendingPush.Clear();
        pendingImport = false;
        nextPollAt = 0;
        unavailableReported = false;
        State = Status.Off;
    }
}
