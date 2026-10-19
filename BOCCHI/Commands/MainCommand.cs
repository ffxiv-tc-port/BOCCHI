using System.Collections.Generic;
using System.Linq;
using BOCCHI.Automation;
using BOCCHI.Modules.Debug;
using ECommons;
using ECommons.DalamudServices;
using Ocelot;
using Ocelot.Commands;
using Ocelot.Modules;

namespace BOCCHI.Commands;

[OcelotCommand]
public class MainCommand(Plugin plugin) : OcelotCommand
{
    protected override string Command
    {
        get => "/bocchi";
    }

    protected override string Description
    {
        get => @"
開關 BOCCHI 主視窗
 - /bocchi：開關主視窗
 - /bocchi config（或 cfg）：開關設定視窗
 - /bocchi stop：全部自動化立刻停止
 - /bocchi stop <項目>：只停一項
 - /bocchi start <項目>：開始一項（巡迴會從頭開始）
   項目：illegal（非法模式）、farm（自動打怪）、treasure（寶箱巡迴）、carrot（幸運胡蘿蔔巡迴）、return（打完自動返回）、leveling（輔助職業練等）
 - /bocchi pause [treasure|carrot]：暫停巡迴、保留進度；不指定就暫停正在跑的那一項
 - /bocchi resume [treasure|carrot]：從目前位置接著跑還沒跑過的點；不指定就續跑唯一暫停中的那一項
   全部停止、無法戰鬥、卡住時巡迴的進度也會保留（不會自己續跑）；換區或登出後只能從頭開始
 - /bocchi restore：恢復倒地時停下的自動化（要先在非法模式設定開啟「死亡後提示恢復自動化」；按下才恢復）
 - /bocchi skip：換一場（略過非法模式目前選定的那一場，馬上改挑下一場；非法模式不停）
 - /bocchi skip clear：清空本場略過（那幾場之後又可以被選到）
 - /bocchi combo：開始常用組合（組合內容在非法模式設定裡勾選）；組合全部在跑時改成停止組合
--------------------------------
".Trim();
    }

    protected override IReadOnlyList<string> Aliases
    {
        get => ["/och", "/occultcrescenthelper"];
    }

    private readonly IReadOnlyList<string> languageCodes =
    [
        "en", "de", "fr", "jp", "uwu",
    ];

    public override void Execute(string command, string arguments)
    {
        if (arguments is "config" or "cfg")
        {
            plugin.Windows.ToggleConfigUI();
            return;
        }

#if DEBUG_BUILD
        if (arguments == "debug")
        {
            plugin.Windows.GetWindow<DebugWindow>().Toggle();
            return;
        }
#endif

        if (arguments == "stop" || arguments.StartsWith("stop "))
        {
            Stop(arguments[4..].Trim());
            return;
        }

        if (arguments == "start" || arguments.StartsWith("start "))
        {
            Start(arguments[5..].Trim());
            return;
        }

        if (arguments == "pause" || arguments.StartsWith("pause "))
        {
            Pause(arguments[5..].Trim());
            return;
        }

        if (arguments == "resume" || arguments.StartsWith("resume "))
        {
            Resume(arguments[6..].Trim());
            return;
        }

        if (arguments == "restore")
        {
            plugin.Automation.RestoreAfterDeath(I18N.T("automation.source.command"), true);
            return;
        }

        if (arguments == "skip")
        {
            plugin.Automation.SkipCurrent(I18N.T("automation.source.command"), true);
            return;
        }

        if (arguments == "skip clear")
        {
            plugin.Automation.ClearSkips(I18N.T("automation.source.command"), true);
            return;
        }

        if (arguments == "combo")
        {
            plugin.Automation.ToggleCombo(I18N.T("automation.source.command"), true);
            return;
        }

        if (arguments == "buff")
        {
            new BuffCommand(plugin).Execute("/bocchibuff", "");
            return;
        }

        if (arguments.StartsWith("tp"))
        {
            new TeleportCommand(plugin).Execute("/bocchitp", arguments.ReplaceFirst("tp", "").Trim());
            return;
        }

        if (arguments.StartsWith("language"))
        {
            var parts = arguments.Split(' ', 2);
            if (parts.Length == 2)
            {
                var code = parts[1].Trim().ToLowerInvariant();
                if (languageCodes.Contains(code))
                {
                    I18N.SetLanguage(code);
                    Svc.Chat.Print(I18N.T("generic.commands.language_set", new Dictionary<string, string> { ["code"] = code }));
                    return;
                }

                Svc.Log.Error($"Unknown language code: {code}");
                return;
            }

            Svc.Chat.Print(I18N.T("generic.commands.language_usage"));
            return;
        }

        plugin.Windows.ToggleMainUI();
    }

    private void Stop(string target)
    {
        var source = I18N.T("automation.source.command");
        if (target.Length == 0)
        {
            var anyRunning = plugin.Automation.IsAnyRunning();
            plugin.Automation.StopAll(source, true);
            if (!anyRunning)
            {
                Svc.Chat.Print(I18N.T("automation.messages.nothing_running"));
            }

            return;
        }

        if (!AutomationKindExtensions.TryParseCommandKey(target, out var kind))
        {
            Svc.Chat.Print(I18N.T("automation.messages.command_usage"));
            return;
        }

        // 單項停止＝結束這一輪：巡迴暫停中的進度也一起放棄。
        var saved = plugin.Automation.GetHunter(kind) is { IsRunning: false, Progress.HasSaved: true } hunter ? hunter.ProgressText : null;
        if (plugin.Automation.Stop(kind, source, true))
        {
            return;
        }

        Svc.Chat.Print(saved != null
            ? I18N.T("automation.messages.progress_abandoned", new Dictionary<string, string> { ["name"] = AutomationControl.GetName(kind), ["progress"] = saved })
            : I18N.T("automation.messages.not_running", new Dictionary<string, string> { ["name"] = AutomationControl.GetName(kind) }));
    }

    private void Pause(string target)
    {
        var source = I18N.T("automation.source.command");
        if (target.Length == 0)
        {
            var paused = false;
            foreach (var candidate in AutomationKindExtensions.All)
            {
                if (candidate.SupportsPause() && plugin.Automation.IsRunning(candidate))
                {
                    paused |= plugin.Automation.Pause(candidate, source, true);
                }
            }

            if (!paused)
            {
                Svc.Chat.Print(I18N.T("automation.messages.nothing_to_pause"));
            }

            return;
        }

        if (!TryParsePausable(target, out var kind))
        {
            return;
        }

        if (!plugin.Automation.Pause(kind, source, true))
        {
            Svc.Chat.Print(I18N.T("automation.messages.not_running", new Dictionary<string, string> { ["name"] = AutomationControl.GetName(kind) }));
        }
    }

    private void Resume(string target)
    {
        var source = I18N.T("automation.source.command");
        if (target.Length == 0)
        {
            AutomationKind? only = null;
            var count = 0;
            foreach (var candidate in AutomationKindExtensions.All)
            {
                if (plugin.Automation.GetHunter(candidate) is { IsRunning: false, Progress.HasSaved: true })
                {
                    only = candidate;
                    count++;
                }
            }

            if (count != 1)
            {
                Svc.Chat.Print(I18N.T(count == 0 ? "automation.messages.nothing_to_resume" : "automation.messages.command_usage"));
                return;
            }

            plugin.Automation.Resume(only!.Value, source, true);
            return;
        }

        if (TryParsePausable(target, out var kind))
        {
            plugin.Automation.Resume(kind, source, true);
        }
    }

    private static bool TryParsePausable(string target, out AutomationKind kind)
    {
        if (!AutomationKindExtensions.TryParseCommandKey(target, out kind))
        {
            Svc.Chat.Print(I18N.T("automation.messages.command_usage"));
            return false;
        }

        if (!kind.SupportsPause())
        {
            Svc.Chat.Print(I18N.T("automation.messages.not_pausable", new Dictionary<string, string> { ["name"] = AutomationControl.GetName(kind) }));
            return false;
        }

        return true;
    }

    private void Start(string target)
    {
        if (!AutomationKindExtensions.TryParseCommandKey(target, out var kind))
        {
            Svc.Chat.Print(I18N.T("automation.messages.command_usage"));
            return;
        }

        plugin.Automation.Start(kind, I18N.T("automation.source.command"), true);
    }
}
