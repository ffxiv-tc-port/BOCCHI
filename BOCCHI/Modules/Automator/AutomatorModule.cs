using System;
using System.Collections.Generic;
using BOCCHI.Automation;
using BOCCHI.Ledger;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using ECommons.DalamudServices;
using Ocelot;
using Ocelot.Modules;
using Ocelot.Windows;

namespace BOCCHI.Modules.Automator;

[OcelotModule(int.MaxValue - 1)]
public class AutomatorModule : Module
{
    public override AutomatorConfig Config
    {
        get => PluginConfig.AutomatorConfig;
    }

    public override bool IsEnabled
    {
        get => Config.IsPropertyEnabled(nameof(Config.Enabled));
    }

    public readonly Automator automator = new();

    public readonly Panel panel = new();

    private readonly List<uint> occultCrescentTerritoryIds = [1252];

    // 戰鬥 AI 是不是這個模組打開的：趕路與停止時只關自己打開的，不動使用者（或 BMR 載入時自動）開的。
    private bool aiEnabledByAutomator;

    private bool aiCommandMissingReported;

    private bool aiNotOursReported;

    private bool aiQueryFailedReported;

    // 簽章逐字對應 BossmodReborn 的 BossMod.AI.IsEnabled() -> bool。
    private ICallGateSubscriber<bool>? bmrAiIsEnabled;

    public AutomatorModule(Plugin plugin, Config config)
        : base(plugin, config)
    {
        config.AutomatorConfig.Enabled = false;
        config.Save();
    }


    public override void Dispose()
    {
        automator.Dispose();
        base.Dispose();
    }

    public override void PostUpdate(UpdateContext context)
    {
        automator.PostUpdate(this, context.Framework);
    }


    // 目前活動畫在主視窗最上方的自動化控制（AutomationPanel），這裡不畫。
    public override bool RenderMainUi(RenderContext context)
    {
        return false;
    }

    public override void OnTerritoryChanged(ushort id)
    {
        if (occultCrescentTerritoryIds.Contains(id))
        {
            return;
        }

        automator.Refresh(ActivityEnd.ZoneChanged);
        Config.Enabled = false;
        PluginConfig.Save();
    }

    public static void ToggleIllegalMode(OcelotPlugin plugin)
    {
        ((Plugin)plugin).Automation.Toggle(AutomationKind.IllegalMode, I18N.T("automation.source.title_button"), false);
    }

    /// <summary>只設旗標；開始前的檢查在 <see cref="AutomationControl.Start"/>。</summary>
    public void EnableIllegalMode()
    {
        var wasDisabled = !Config.Enabled;
        Config.Enabled = true;

        if (wasDisabled)
        {
            Svc.Chat.Print(T("messages.on"));
        }
    }

    /// <summary>只重設本模組的狀態（冪等）；動作鏈與導航由 <see cref="AutomationControl"/> 收。</summary>
    /// <param name="sendCommands">false＝不送關閉戰鬥 AI 的指令（遊戲關閉中）。</param>
    public void DisableIllegalMode(bool sendCommands = true)
    {
        DisableIllegalMode(sendCommands, ActivityEnd.ManualStop);
    }

    /// <param name="end">活動帳記的結束原因。</param>
    public void DisableIllegalMode(bool sendCommands, ActivityEnd end)
    {
        Config.Enabled = false;
        automator.Refresh(end);

        if (aiEnabledByAutomator && sendCommands)
        {
            SetAi(false, "非法模式停止");
        }

        aiEnabledByAutomator = false;
        aiNotOursReported = false;
    }

    /// <summary>
    /// 開關設定裡選的戰鬥 AI；只在 framework 執行緒呼叫。開：已經開著就不送（重送 /bmrai on 會重建 AI、清掉它的自動循環預設）。
    /// 關：只關本模組開的。那個插件沒載入時不送（送了只會在聊天視窗得到「沒有這個指令」）。
    /// </summary>
    /// <param name="reason">寫進 log 的原因；每幀呼叫的地方要傳常數，不要內插。</param>
    public void SetAi(bool on, string reason)
    {
        var current = QueryAiEnabled();
        if (on)
        {
            if (current == true || (current == null && aiEnabledByAutomator))
            {
                return;
            }
        }
        else
        {
            if (!aiEnabledByAutomator)
            {
                if (current == true && !aiNotOursReported)
                {
                    aiNotOursReported = true;
                    Svc.Log.Information($"[BOCCHI][戰鬥 AI] 不關閉 {Config.AiProvider.ToLabel()} 的 AI：它不是 BOCCHI 開的（你自己、其他插件或 BMR 載入時自動開的），非法模式趕路與停止時都保持開著。（{reason}）");
                }

                return;
            }

            if (current == false)
            {
                aiEnabledByAutomator = false;
                Svc.Log.Information($"[BOCCHI][戰鬥 AI] BOCCHI 開的 AI 已經被別人關掉，不再送關閉指令。（{reason}）");
                return;
            }
        }

        var command = Config.AiProvider.Command(on);
        if (!Svc.Commands.ProcessCommand(command))
        {
            if (!aiCommandMissingReported)
            {
                aiCommandMissingReported = true;
                Svc.Log.Information($"[BOCCHI][非法模式] 找不到指令 {command}（設定的戰鬥 AI 插件沒有安裝或沒有載入），非法模式不會切換戰鬥 AI。");
            }

            return;
        }

        aiEnabledByAutomator = on;
        var before = current switch { true => "開", false => "關", _ => "?" };
        Svc.Log.Information($"[BOCCHI][戰鬥 AI] {(on ? "開啟" : "關閉")} {Config.AiProvider.ToLabel()} 的 AI（{command}，原本：{before}）：{reason}");
    }

    // 只問 BMR（VBM 也用 BossMod. 前綴，選 VBM 時問到的可能是 BMR）；問不到回 null，呼叫端退回舊行為。
    private bool? QueryAiEnabled()
    {
        if (Config.AiProvider != AiType.BMR)
        {
            return null;
        }

        try
        {
            bmrAiIsEnabled ??= Svc.PluginInterface.GetIpcSubscriber<bool>("BossMod.AI.IsEnabled");
            return bmrAiIsEnabled.InvokeFunc();
        }
        catch (IpcNotReadyError)
        {
            return null;
        }
        catch (Exception ex)
        {
            if (!aiQueryFailedReported)
            {
                aiQueryFailedReported = true;
                Svc.Log.Information($"[BOCCHI][戰鬥 AI] 問不到 BossMod.AI.IsEnabled（{ex.GetType().Name}）：{ex.Message}；改用 BOCCHI 自己記得的狀態。");
            }

            return null;
        }
    }
}
