using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BOCCHI.Chains;
using BOCCHI.Data;
using BOCCHI.Ledger;
using BOCCHI.Modules.Automator;
using BOCCHI.Modules.Carrots;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.MobFarmer;
using BOCCHI.Modules.PhantomJobs;
using BOCCHI.Modules.StateManager;
using BOCCHI.Modules.Teleporter;
using BOCCHI.Modules.Treasure;
using BOCCHI.Pathfinding;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Ocelot;
using Ocelot.Chain;
using Ocelot.IPC;
using Ocelot.Modules;

namespace BOCCHI.Automation;

public sealed partial class AutomationControl
{
    /// <summary>主視窗「換一場」鈕能不能按；不能時 <paramref name="whyNotKey"/> 是原因的翻譯鍵（不組字串，每幀可呼叫）。</summary>
    public bool CanSkipCurrent(out string whyNotKey)
    {
        if (automator == null || !IsRunning(AutomationKind.IllegalMode))
        {
            whyNotKey = "automation.skip.blocked_not_running";
            return false;
        }

        var current = automator.automator.Activity;
        if (current == null)
        {
            whyNotKey = "automation.skip.blocked_no_activity";
            return false;
        }

        if (!global::BOCCHI.Modules.Automator.Automator.TryGetInstance(current, out _))
        {
            whyNotKey = "automation.skip.blocked_pot_wait";
            return false;
        }

        whyNotKey = string.Empty;
        return true;
    }

    /// <summary>
    /// 換一場（主視窗按鈕、/bocchi skip）：略過非法模式目前選定的那一場、馬上改挑下一場。非法模式不停，輔助職業技能清單（WrathCombo 的租約）不交還。
    /// 只在 framework 執行緒呼叫；回傳是否真的略過了。成功時一律在聊天視窗說明接下來會怎樣；沒略過時只有 <paramref name="announce"/> 才說。
    /// </summary>
    public bool SkipCurrent(string source, bool announce)
    {
        if (!CanSkipCurrent(out var whyNotKey) && whyNotKey == "automation.skip.blocked_not_running")
        {
            Svc.Log.Information($"[BOCCHI][換一場] 來源={source} 沒有略過：非法模式沒有在執行");
            if (announce)
            {
                Svc.Chat.Print(I18N.T("automation.messages.skip_nothing", new Dictionary<string, string> { ["reason"] = I18N.T(whyNotKey) }));
            }

            return false;
        }

        var outcome = automator!.automator.SkipCurrent(automator, source, out var name, out var place);
        if (outcome != SkipOutcome.Skipped)
        {
            if (announce)
            {
                var key = outcome == SkipOutcome.NoActivity ? "automation.skip.blocked_no_activity" : "automation.skip.blocked_pot_wait";
                Svc.Chat.Print(I18N.T("automation.messages.skip_nothing", new Dictionary<string, string> { ["reason"] = I18N.T(key) }));
            }

            return false;
        }

        var message = place switch
        {
            SkipPlace.InsideFate => "automation.messages.skip_done_in_fate",
            SkipPlace.InsideCriticalEncounter => "automation.messages.skip_done_in_ce",
            SkipPlace.InCombat => "automation.messages.skip_done_in_combat",
            _ => "automation.messages.skip_done",
        };
        Svc.Chat.Print(I18N.T(message, new Dictionary<string, string> { ["name"] = name }));
        return true;
    }

    /// <summary>本場略過的場數（主視窗每幀讀）。</summary>
    public int SkipCount
    {
        get => automator?.automator.SkipCount ?? 0;
    }

    /// <summary>本場略過的名稱，一行一場（說明框用；會配置，只在要顯示時呼叫）。</summary>
    public string SkippedNames()
    {
        if (automator == null || automator.automator.SkipCount == 0)
        {
            return string.Empty;
        }

        var skips = automator.automator.Skips;
        var names = new List<string>(skips.Count);
        for (var i = 0; i < skips.Count; i++)
        {
            names.Add($"・{skips.NameAt(i)}");
        }

        return string.Join("\n", names);
    }

    /// <summary>使用者清空本場略過（主視窗、/bocchi skip clear）：之後那幾場又可以被選到。只在 framework 執行緒呼叫。</summary>
    public int ClearSkips(string source, bool announce)
    {
        var count = automator?.automator.ClearSkips(I18N.T("automation.skip.cleared_by", new Dictionary<string, string> { ["source"] = source })) ?? 0;
        if (announce)
        {
            Svc.Chat.Print(I18N.T("automation.messages.skips_cleared", new Dictionary<string, string> { ["count"] = count.ToString() }));
        }

        return count;
    }

    // 非法模式開始（已成功）時：停止後很快又開始＝換一場，停止當下選定的那一場加進本場略過（設定可關）。
    private void ApplyQuickRestartSkip(string source)
    {
        if (automator?.automator.ApplyQuickRestart(automator, source, out var elapsedMs) is not { } name)
        {
            return;
        }

        Svc.Chat.Print(I18N.T("automation.messages.skip_quick_restart", new Dictionary<string, string>
        {
            ["seconds"] = Math.Max(1, (elapsedMs + 999) / 1000).ToString(),
            ["name"] = name,
        }));
    }
}
