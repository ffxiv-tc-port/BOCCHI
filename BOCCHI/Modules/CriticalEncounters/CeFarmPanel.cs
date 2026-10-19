using System;
using System.Collections.Generic;
using System.Linq;
using BOCCHI.Data;
using BOCCHI.Windows;
using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using Ocelot;

namespace BOCCHI.Modules.CriticalEncounters;

/// <summary>
/// 主視窗緊急遭遇戰區段下方的「跟團刷 CE」：沒武裝時選目標、輪數、按武裝；武裝中顯示進度與解除鈕，進島待命時多一顆「出發」。
/// 只在框架執行緒（主視窗繪製）用；文字只在語言、選擇或進度變了才重組。
/// </summary>
public sealed class CeFarmPanel
{
    // 兩歧塔（48）不是一般的緊急遭遇戰：不能報名跑過去。
    private static readonly uint[] Targets = EventData.CriticalEncounters.Keys.Where(static id => id != 48).OrderBy(static id => id).ToArray();

    private readonly Dictionary<uint, string> optionLabels = [];

    private string? template;

    private string title = string.Empty;

    private string roundsLabel = string.Empty;

    private string armLabel = string.Empty;

    private string disarmLabel = string.Empty;

    private string startLabel = string.Empty;

    private string status = string.Empty;

    private long statusAt;

    private CeFarmPhase statusPhase;

    private int statusDone = -1;

    public void Draw(CriticalEncountersModule module)
    {
        var farm = module.Farm;
        RefreshLabels(module);
        MainPanelUi.Title(title, true);
        ImGui.SameLine();
        if (farm.Armed)
        {
            DrawArmed(module, farm);
            return;
        }

        DrawDisarmed(module, farm);
    }

    private void RefreshLabels(CriticalEncountersModule module)
    {
        var current = module.T("panel.farm.title");
        if (ReferenceEquals(current, template))
        {
            return;
        }

        template = current;
        title = current;
        roundsLabel = module.T("panel.farm.rounds");
        armLabel = module.T("panel.farm.arm") + "##ce-farm-arm";
        disarmLabel = module.T("panel.farm.disarm") + "##ce-farm-disarm";
        startLabel = module.T("panel.farm.start") + "##ce-farm-start";
        optionLabels.Clear();
        statusDone = -1;
    }

    private void DrawDisarmed(CriticalEncountersModule module, CeFarm farm)
    {
        var config = module.Config;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
        var preview = config.CeFarmTarget == 0 ? module.T("panel.farm.pick") : CeFarm.CeName(config.CeFarmTarget);
        if (ImGui.BeginCombo("##ce-farm-target", preview, ImGuiComboFlags.HeightLarge))
        {
            foreach (var id in Targets)
            {
                if (ImGui.Selectable(OptionLabel(id), id == config.CeFarmTarget) && id != config.CeFarmTarget)
                {
                    config.CeFarmTarget = id;
                    module.PluginConfig.Save();
                    Svc.Log.Information($"[BOCCHI][跟團刷CE] 目標改成「{CeFarm.CeName(id)}」({id})（主視窗）：下一次武裝生效");
                }
            }

            ImGui.EndCombo();
        }
        else if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(module.T("panel.farm.target_tooltip"));
        }

        MainPanelUi.Gap();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(roundsLabel);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5);
        var rounds = config.CeFarmRounds;
        if (ImGui.InputInt("##ce-farm-rounds", ref rounds, 1, 5))
        {
            rounds = Math.Clamp(rounds, 0, 999);
            if (rounds != config.CeFarmRounds)
            {
                config.CeFarmRounds = rounds;
                module.PluginConfig.Save();
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(module.T("panel.farm.rounds_tooltip"));
        }

        MainPanelUi.Gap();
        if (ImGui.Button(armLabel))
        {
            farm.Arm(I18N.T("automation.source.main_window"));
        }

        if (ImGui.IsItemHovered())
        {
            var why = farm.ArmBlockText();
            var tip = module.T("panel.farm.arm_tooltip").Replace("{minutes}", config.CeFarmWaitMinutes.ToString());
            ImGui.SetTooltip(why == null ? tip : tip + "\n\n" + module.T("panel.farm.blocked").Replace("{reason}", why));
        }
    }

    private void DrawArmed(CriticalEncountersModule module, CeFarm farm)
    {
        RefreshStatus(module, farm);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(status);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(module.T("panel.farm.status_tooltip"));
        }

        ImGui.SameLine();
        if (farm.Phase == CeFarmPhase.Ready)
        {
            if (ImGui.Button(startLabel))
            {
                farm.Start(I18N.T("automation.source.main_window"));
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(module.T("panel.farm.start_tooltip").Replace("{name}", farm.TargetName).Replace("{minutes}", module.Config.CeFarmWaitMinutes.ToString()));
            }

            ImGui.SameLine();
        }

        if (ImGui.Button(disarmLabel))
        {
            farm.Disarm(CeFarmStop.Manual);
        }
    }

    // 等開放報名時每秒更新一次（已等時間），其他狀態只在換段或輪數變了才重組。
    private void RefreshStatus(CriticalEncountersModule module, CeFarm farm)
    {
        var now = Environment.TickCount64;
        var phase = farm.Phase;
        if (phase == statusPhase && farm.RoundsDone == statusDone && (phase != CeFarmPhase.Waiting || now - statusAt < 1_000))
        {
            return;
        }

        statusPhase = phase;
        statusDone = farm.RoundsDone;
        statusAt = now;
        var key = phase switch
        {
            CeFarmPhase.Ready => "panel.farm.status.ready",
            CeFarmPhase.Waiting => "panel.farm.status.waiting",
            CeFarmPhase.Joined => "panel.farm.status.joined",
            CeFarmPhase.ExitDelay or CeFarmPhase.Exiting => "panel.farm.status.exiting",
            _ => "panel.farm.status.away",
        };
        var waited = Math.Max(0, farm.WaitedMs) / 1000;
        status = module.T(key)
            .Replace("{round}", (farm.RoundsDone + 1).ToString())
            .Replace("{done}", farm.RoundsDone.ToString())
            .Replace("{limit}", farm.RoundLimit > 0 ? farm.RoundLimit.ToString() : module.T("panel.farm.no_limit"))
            .Replace("{name}", farm.TargetName)
            .Replace("{waited}", $"{waited / 60}:{waited % 60:00}")
            .Replace("{minutes}", module.Config.CeFarmWaitMinutes.ToString());
    }

    private string OptionLabel(uint id)
    {
        if (!optionLabels.TryGetValue(id, out var label))
        {
            label = $"{CeFarm.CeName(id)}##ce-farm-{id}";
            optionLabels[id] = label;
        }

        return label;
    }
}
