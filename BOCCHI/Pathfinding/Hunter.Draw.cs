using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using BOCCHI.Automation;
using BOCCHI.Chains;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules;
using BOCCHI.Modules.Pathfinder;
using BOCCHI.Modules.StateManager;
using BOCCHI.Windows;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Bindings.ImGui;
using Ocelot;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;
using Ocelot.IPC;
using Ocelot.Modules;
using TextCopy;

namespace BOCCHI.Pathfinding;

public abstract partial class Hunter
{
    // 開始／停止在主視窗最上方的自動化控制；這裡只在跑過或正在跑時顯示經過時間、匯出與進度。
    public void Draw(Module<Plugin, Config> module)
    {
        var elapsed = stopwatch.Elapsed;
        var hasElapsed = elapsed > TimeSpan.Zero;
        var showProgress = running && Steps.Count > 0;
        var showExtra = HasExtraRows;
        if (!hasElapsed && !showProgress && !showExtra)
        {
            return;
        }

        MainPanelUi.Title(module.T("panel.hunt.title"), hasElapsed);
        if (hasElapsed)
        {
            ImGui.SameLine();
            MainPanelUi.Labelled(I18N.T("hunter.elapsed"), $"{elapsed:mm\\:ss}");
            ImGui.SameLine();
            if (ImGui.Button(I18N.T("hunter.export.label")))
            {
                ClipboardService.SetText(JSON);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(I18N.T("hunter.export.tooltip"));
            }
        }

        DrawRange(module);

        if (showProgress)
        {
            DrawProgress(module);
        }

        if (showExtra)
        {
            DrawExtraRows(module);
        }
    }

    private void DrawProgress(Module<Plugin, Config> module)
    {
        ImGui.Indent(16);
        MainPanelUi.Labelled(I18N.T("hunter.progress"), $"{stepIndex}/{Steps.Count}");

        // 最後一步做完到下一拍收尾之間 stepIndex 等於步數，這時沒有目前這一步。
        if (stepIndex < Steps.Count)
        {
            var step = CurrentStep.Type;
            if (step == PathfinderStepType.WalkToNode)
            {
                MainPanelUi.Gap();
                MainPanelUi.Labelled(module.T("panel.hunt.distance_node"), $"{distance:f2}/{GetDetectionRange():f2}");
            }
            else if (step == PathfinderStepType.WalkToAethernet)
            {
                MainPanelUi.Gap();
                MainPanelUi.Labelled(I18N.T("hunter.distance_shard"), $"{distance:f2}");
            }
        }

        ImGui.Unindent(16);
    }

    // 「本輪 範圍內／全部 點（上限與來源）」；還沒規劃畫灰色 ?，分母與來源的說明放說明框。
    private void DrawRange(Module<Plugin, Config> module)
    {
        ImGui.Indent(16);
        var label = I18N.T("hunter.range.label");
        if (range.Total <= 0)
        {
            ImGui.TextColored(OcelotColor.Yellow, $"{label}:");
            ImGui.SameLine();
            ImGui.TextColored(UnknownColor, "?");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(I18N.T("hunter.range.unknown_tooltip"));
            }
        }
        else
        {
            RefreshRangeText(module);
            MainPanelUi.Labelled(label, rangeText);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(rangeTooltip);
            }
        }

        ImGui.Unindent(16);
    }

    private void RefreshRangeText(Module<Plugin, Config> module)
    {
        var template = I18N.T("hunter.range.value");
        if (rangeTextVersion == rangeVersion && ReferenceEquals(template, rangeTemplate))
        {
            return;
        }

        rangeTextVersion = rangeVersion;
        rangeTemplate = template;
        rangeText = template
            .Replace("{in}", range.InRange.ToString())
            .Replace("{total}", range.Total.ToString())
            .Replace("{max}", range.MaxLevel.ToString())
            .Replace("{source}", I18N.T(SourceKey(range.Source)));
        if (HasDirection)
        {
            rangeText += I18N.T("hunter.range.direction").Replace("{direction}", I18N.T(roundReverse ? "hunter.direction.reverse" : "hunter.direction.forward"));
        }

        rangeText += RoundRangeSuffix(module);
        rangeTooltip = module.T("panel.hunt.range.tooltip")
            .Replace("{cap}", KnowledgeLevel.SouthHornCap.ToString())
            .Replace("{ceiling}", PathfinderConfig.LevelCeiling.ToString());
    }

    private static string SourceKey(MaxLevelSource source)
    {
        return source switch
        {
            MaxLevelSource.FollowKnowledge => "hunter.range.source.follow",
            MaxLevelSource.KnowledgeAtCap => "hunter.range.source.at_cap",
            MaxLevelSource.KnowledgeUnavailable => "hunter.range.source.unavailable",
            _ => "hunter.range.source.manual",
        };
    }

    private void SetRange(HuntRange next)
    {
        range = next;
        rangeVersion++;
    }

    // 只有跟隨知見等級時才讀知見等級（規劃時一次，不是每幀）。
    private MaxLevelChoice ResolveMaxLevel()
    {
        var knowledge = config.FollowKnowledgeLevel ? KnowledgeLevel.Read() : default;
        var choice = PathfinderConfig.ChooseMaxLevel(config.FollowKnowledgeLevel, config.MaxLevel, knowledge);
        Svc.Log.Information(choice.Source switch
        {
            MaxLevelSource.FollowKnowledge => $"[BOCCHI][尋路] 區域等級上限 {choice.Level}（跟隨真實知見等級 {knowledge.Real}）",
            MaxLevelSource.KnowledgeAtCap => $"[BOCCHI][尋路] 區域等級上限 {choice.Level}（真實知見等級 {knowledge.Real} 已滿級（上限 {KnowledgeLevel.SouthHornCap}），改用設定值）",
            MaxLevelSource.KnowledgeUnavailable => $"[BOCCHI][尋路] 區域等級上限 {choice.Level}（讀不到知見等級，沿用設定值）",
            _ => $"[BOCCHI][尋路] 區域等級上限 {choice.Level}（設定值，沒有跟隨知見等級）",
        });
        return choice;
    }
}
