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
    /// <summary>常用組合（非法模式設定裡勾選）包不包含這一項；非法模式模組沒載入時一項都不包含。</summary>
    public bool InCombo(AutomationKind kind)
    {
        var config = automator?.Config;
        return config != null && kind switch
        {
            AutomationKind.IllegalMode => config.ComboIllegalMode,
            AutomationKind.MobFarmer => config.ComboMobFarmer,
            AutomationKind.TreasureHunt => config.ComboTreasureHunt,
            AutomationKind.CarrotHunt => config.ComboCarrotHunt,
            AutomationKind.ReturnAfterEvents => config.ComboReturnAfterEvents,
            AutomationKind.PhantomJobLeveling => config.ComboPhantomJobLeveling,
            _ => false,
        };
    }

    /// <summary>常用組合鈕現在的狀態；每幀可呼叫（不組原因字串，要說明時另外問 <see cref="ComboWhyNot"/>）。</summary>
    public ComboStatus GetComboStatus()
    {
        if (automator == null)
        {
            return ComboStatus.Unavailable;
        }

        var count = 0;
        var movement = 0;
        var allRunning = true;
        var blocked = false;
        foreach (var kind in AutomationKindExtensions.All)
        {
            if (!InCombo(kind))
            {
                continue;
            }

            count++;
            if (kind.DrivesMovement())
            {
                movement++;
            }

            if (IsRunning(kind))
            {
                continue;
            }

            allRunning = false;
            blocked |= !CanStart(kind, out _);
        }

        if (count == 0)
        {
            return ComboStatus.Empty;
        }

        if (movement > 1)
        {
            return ComboStatus.Conflict;
        }

        if (allRunning)
        {
            return ComboStatus.Running;
        }

        return blocked ? ComboStatus.Blocked : ComboStatus.Ready;
    }

    /// <summary>組合裡的項目（短名，以「、」分隔）；會配置，只在要顯示時呼叫。</summary>
    public string ComboNames()
    {
        return string.Join("、", AutomationKindExtensions.All.Where(InCombo).Select(k => I18N.T(k.ShortNameKey())));
    }

    /// <summary>組合鈕不能按的原因（給說明框與聊天視窗）；會配置，只在要顯示時呼叫。</summary>
    public string ComboWhyNot(ComboStatus status)
    {
        switch (status)
        {
            case ComboStatus.Unavailable:
                return I18N.T("automation.blocked.module_unavailable");
            case ComboStatus.Empty:
                return I18N.T("automation.combo.empty");
            case ComboStatus.Conflict:
                var moving = AutomationKindExtensions.All.Where(k => InCombo(k) && k.DrivesMovement()).Select(k => I18N.T(k.ShortNameKey()));
                return I18N.T("automation.combo.conflict", new Dictionary<string, string> { ["names"] = string.Join("、", moving) });
            case ComboStatus.Blocked:
                var lines = new List<string>();
                foreach (var kind in AutomationKindExtensions.All)
                {
                    if (InCombo(kind) && !IsRunning(kind) && !CanStart(kind, out var why))
                    {
                        lines.Add(I18N.T("automation.combo.blocked_item", new Dictionary<string, string> { ["name"] = I18N.T(kind.ShortNameKey()), ["reason"] = why }));
                    }
                }

                return string.Join("\n", lines);
            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// 開始常用組合：組合裡還沒在跑的項目照一般的開始條件逐項開始（已在跑的不動）。有任何一項開不起來就一項都不開，並說原因。
    /// 一律手動（主視窗按鈕、/bocchi combo）；只在 framework 執行緒呼叫。回傳是否開始了至少一項。
    /// </summary>
    public bool StartCombo(string source, bool announce)
    {
        var status = GetComboStatus();
        var skipCount = SkipCount;
        if (status != ComboStatus.Ready)
        {
            var why = status == ComboStatus.Running ? I18N.T("automation.combo.all_running") : ComboWhyNot(status);
            Svc.Log.Information($"[BOCCHI][組合開始] 來源={source} 沒有開始（{status}）：{why.Replace("\n", "；")} 組合=「{ComboNames()}」 略過集合={skipCount}");
            Svc.Chat.Print(I18N.T("automation.messages.combo_cannot_start", new Dictionary<string, string> { ["reason"] = why.Replace("\n", " ") }));
            return false;
        }

        var label = I18N.T("automation.source.combo", new Dictionary<string, string> { ["source"] = source });
        var already = new List<string>();
        var started = new List<string>();
        var failed = new List<string>();
        foreach (var kind in AutomationKindExtensions.All)
        {
            if (!InCombo(kind))
            {
                continue;
            }

            var name = I18N.T(kind.ShortNameKey());
            if (IsRunning(kind))
            {
                already.Add(name);
                continue;
            }

            (Start(kind, label, false) ? started : failed).Add(name);
        }

        Svc.Log.Information($"[BOCCHI][組合開始] 來源={source} 組合=「{ComboNames()}」 開始=「{string.Join("、", started)}」 已在跑=「{string.Join("、", already)}」 沒開成=「{string.Join("、", failed)}」 略過集合={SkipCount}");
        if (announce && started.Count > 0)
        {
            Svc.Chat.Print(I18N.T("automation.messages.combo_started", new Dictionary<string, string> { ["names"] = string.Join("、", started) }));
        }

        return started.Count > 0;
    }

    /// <summary>停止常用組合：只停組合裡正在跑的項目，其他自動化照舊。只在 framework 執行緒呼叫；回傳是否停了至少一項。</summary>
    public bool StopCombo(string source, bool announce)
    {
        var label = I18N.T("automation.source.combo", new Dictionary<string, string> { ["source"] = source });
        var stopped = new List<string>();
        foreach (var kind in AutomationKindExtensions.All)
        {
            if (InCombo(kind) && IsRunning(kind) && Stop(kind, label, false))
            {
                stopped.Add(I18N.T(kind.ShortNameKey()));
            }
        }

        Svc.Log.Information($"[BOCCHI][組合停止] 來源={source} 組合=「{ComboNames()}」 停止=「{string.Join("、", stopped)}」");
        if (announce)
        {
            Svc.Chat.Print(stopped.Count > 0
                ? I18N.T("automation.messages.combo_stopped", new Dictionary<string, string> { ["names"] = string.Join("、", stopped) })
                : I18N.T("automation.messages.combo_nothing_running"));
        }

        return stopped.Count > 0;
    }

    /// <summary>組合裡的項目全部在跑時停止組合，否則開始組合。</summary>
    public void ToggleCombo(string source, bool announce)
    {
        if (GetComboStatus() == ComboStatus.Running)
        {
            StopCombo(source, announce);
            return;
        }

        StartCombo(source, announce);
    }
}
