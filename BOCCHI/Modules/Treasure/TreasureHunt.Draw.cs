using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BOCCHI.Automation;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.Pathfinder;
using BOCCHI.Pathfinding;
using BOCCHI.Windows;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.ImGuiMethods;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Ocelot;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;
using Ocelot.Modules;
using NativeTreasure = FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace BOCCHI.Modules.Treasure;

public partial class TreasureHunt
{
    protected override bool HasExtraRows
    {
        get => true;
    }

    protected override void DrawExtraRows(Module<Plugin, Config> owner)
    {
        var memory = module.HuntMemory;
        RefreshSummary(memory);

        ImGui.Indent(16);
        (senseRow ??= new TreasureSenseRow()).Draw(module, UnknownColor);
        MainPanelUi.Labelled(module.T("panel.hunt.memory.chests"), summaryChests);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(module.T("panel.hunt.memory.tooltip"));
        }

        MainPanelUi.Gap();
        if (memory.HasSpawnPoints)
        {
            MainPanelUi.Labelled(module.T("panel.hunt.memory.visited"), summaryVisited);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(memory.Participating ? module.T("panel.hunt.memory.participating_tooltip") : module.T("panel.hunt.memory.tooltip"));
            }
        }
        else
        {
            // 生成點還沒讀到時不記巡過：畫成 ? 而不是 0。
            ImGui.TextColored(OcelotColor.Yellow, $"{module.T("panel.hunt.memory.visited")}:");
            ImGui.SameLine();
            ImGui.TextColored(UnknownColor, "?");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(module.T("panel.hunt.memory.unknown_tooltip"));
            }
        }

        ImGui.SameLine();
        var any = memory.ChestCount > 0 || memory.VisitedCount > 0;
        if (ImGuiEx.IconButton(ClearIcon, "treasure-hunt-memory-clear", enabled: any))
        {
            module.ClearHuntMemory(I18N.T("automation.source.main_window"));
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(module.T("panel.hunt.memory.clear_tooltip"));
        }

        DrawDirection();
        DrawRoutes();
        DrawCarrotToggle();
        ImGui.Unindent(16);
    }

    // 同設定視窗「順路挖幸運胡蘿蔔」；改了不必重新開始，之後看到的胡蘿蔔就照新的設定。
    private void DrawCarrotToggle()
    {
        var config = module.Config;
        var template = module.T("panel.hunt.carrots.label");
        if (!ReferenceEquals(template, carrotTemplate) || carrotTooltipMax != config.HuntCarrotMaxDistance)
        {
            carrotTemplate = template;
            carrotTooltipMax = config.HuntCarrotMaxDistance;
            carrotLabel = template + "##treasure-hunt-carrots";
            carrotTooltip = module.T("panel.hunt.carrots.tooltip").Replace("{max}", carrotTooltipMax.ToString());
        }

        var on = config.HuntDigCarrots;
        if (ImGui.Checkbox(carrotLabel, ref on))
        {
            config.HuntDigCarrots = on;
            module.PluginConfig.Save();
            Svc.Log.Information($"[BOCCHI][順路胡蘿蔔] 「順路挖幸運胡蘿蔔」改成「{(on ? "開" : "關")}」（主視窗）：距離上限 {config.HuntCarrotMaxDistance} 米；之後看到的胡蘿蔔照新的設定（已經插隊的那一根照樣做完）");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(carrotTooltip);
        }
    }

    /// <summary>由模組每幀呼叫（顯示寶箱巡迴、在島上時）；實際每秒最多重算一次。</summary>
    public void UpdateRouteBoard()
    {
        Menu.Board.Update(module.HuntMemory, module.PluginConfig.PathfinderConfig, TreasureRouteBoard.SelectionOf(module.Config.HuntRoutesExcluded), Environment.TickCount64);
    }

    private CarrotDetour Carrots
    {
        get => carrotDetour ??= new CarrotDetour();
    }

    private RouteMenu Menu
    {
        get => routeMenu ??= new RouteMenu();
    }

    // 「路線」選單的板子與文字快取（第一次用到才建）；文字只在板子、選擇、設定或語言變了才重組。
    private sealed class RouteMenu
    {
        public readonly TreasureRouteBoard Board = new();

        public readonly string[] Labels = new string[TreasureRoutes.Order.Length];

        public readonly string[] Values = new string[TreasureRoutes.Order.Length];

        public readonly string[] Tips = new string[TreasureRoutes.Order.Length];

        public string Preview = string.Empty;

        public string PreviewTip = string.Empty;

        public string AllLabel = string.Empty;

        public string AllTip = string.Empty;

        public string OnlyLabel = string.Empty;

        public string OnlyTip = string.Empty;

        public string Other = string.Empty;

        public string OtherTip = string.Empty;

        public int Version = -1;

        public int Selection = -1;

        public bool Only;

        public string? Template;
    }

    // 開始前就選得到；改了從下一次按開始生效，這一輪（含暫停後續跑）照開始時選的路線。至少要留一段。
    private void DrawRoutes()
    {
        var config = module.Config;
        var menu = Menu;
        var selection = TreasureRouteBoard.SelectionOf(config.HuntRoutesExcluded);
        RefreshRouteTexts(menu, selection, config.HuntChestsInRoutesOnly);

        MainPanelUi.Gap();
        ImGui.TextColored(OcelotColor.Yellow, $"{module.T("panel.hunt.routes.label")}:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
        if (ImGui.BeginCombo("##treasure-hunt-routes", menu.Preview, ImGuiComboFlags.HeightLarge))
        {
            DrawRouteMenu(menu, config, selection);
            ImGui.EndCombo();
        }
        else if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(menu.PreviewTip);
        }

        if (selection != TreasureRouteBoard.All && menu.Board.OtherRouteChests > 0)
        {
            ImGui.TextUnformatted(menu.Other);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(menu.OtherTip);
            }
        }
    }

    private void DrawRouteMenu(RouteMenu menu, TreasureConfig config, int selection)
    {
        var all = selection == TreasureRouteBoard.All;
        var allOn = all;
        if (ImGui.Checkbox(menu.AllLabel, ref allOn) && allOn && !all)
        {
            config.HuntRoutesExcluded.Clear();
            RoutesChanged(menu, TreasureRouteBoard.All);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(menu.AllTip);
        }

        ImGui.Separator();
        if (ImGui.BeginTable("##treasure-hunt-route-table", 2, ImGuiTableFlags.SizingFixedFit))
        {
            var count = BitOperations.PopCount((uint)selection);
            for (var i = 0; i < TreasureRoutes.Order.Length; i++)
            {
                var route = TreasureRoutes.Order[i];
                var on = TreasureRouteBoard.IsSelected(selection, route);
                var next = on;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Checkbox(menu.Labels[i], ref next) && next != on && (next || count > 1))
                {
                    if (next)
                    {
                        config.HuntRoutesExcluded.RemoveAll(excluded => excluded == route);
                    }
                    else
                    {
                        config.HuntRoutesExcluded.Add(route);
                    }

                    RoutesChanged(menu, next ? selection | (1 << i) : selection & ~(1 << i));
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(menu.Tips[i]);
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(menu.Values[i]);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(menu.Tips[i]);
                }
            }

            ImGui.EndTable();
        }

        ImGui.Separator();
        var only = config.HuntChestsInRoutesOnly;
        if (ImGui.Checkbox(menu.OnlyLabel, ref only))
        {
            config.HuntChestsInRoutesOnly = only;
            module.PluginConfig.Save();
            menu.Board.RefreshSoon();
            var current = IsRunning || Progress.HasSaved ? $"；這一輪（含續跑）照原本的「{(roundChestsInRoutesOnly ? "開" : "關")}」" : string.Empty;
            Svc.Log.Information($"[BOCCHI][寶箱巡迴] 「只開所選路線內的已記錄寶箱」改成「{(only ? "開" : "關")}」（主視窗）：下一次按開始生效{current}");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(menu.OnlyTip);
        }
    }

    private void RoutesChanged(RouteMenu menu, int selection)
    {
        module.PluginConfig.Save();
        menu.Board.RefreshSoon();
        var current = IsRunning || Progress.HasSaved ? $"；這一輪（含續跑）照原本的「{RouteNames(RoundSelection)}」" : string.Empty;
        Svc.Log.Information($"[BOCCHI][寶箱巡迴] 路線改成「{RouteNames(selection)}」（主視窗）：下一次按開始生效{current}");
    }

    private void RefreshRouteTexts(RouteMenu menu, int selection, bool only)
    {
        var template = module.T("panel.hunt.routes.value");
        var board = menu.Board;
        if (menu.Version == board.Version && menu.Selection == selection && menu.Only == only && ReferenceEquals(template, menu.Template))
        {
            return;
        }

        menu.Version = board.Version;
        menu.Selection = selection;
        menu.Only = only;
        menu.Template = template;
        var count = BitOperations.PopCount((uint)selection);
        var maxLevel = board.MaxLevel > 0 ? board.MaxLevel : PathfinderConfig.LevelCeiling;
        var tipTemplate = module.T("panel.hunt.routes.route_tooltip");
        for (var i = 0; i < TreasureRoutes.Order.Length; i++)
        {
            var row = board.Rows[i];
            var name = board.Name(i);
            menu.Labels[i] = $"{name}##treasure-hunt-route-{i}";
            menu.Values[i] = template
                .Replace("{count}", Known(row.Count))
                .Replace("{left}", Known(row.Remaining))
                .Replace("{time}", Clock(row.RoundSeconds));
            var tip = tipTemplate
                .Replace("{name}", name)
                .Replace("{count}", Known(row.Count))
                .Replace("{max}", board.MaxLevel > 0 ? board.MaxLevel.ToString() : "?")
                .Replace("{left}", Known(row.Remaining))
                .Replace("{left_time}", Clock(row.RemainingSeconds))
                .Replace("{time}", Clock(row.RoundSeconds))
                .Replace("{speed}", TreasureRouteStats.MetersPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("{nodes}", string.Join("、", TreasureRoutes.NodesOf(TreasureRoutes.Order[i], maxLevel)));
            menu.Tips[i] = count == 1 && TreasureRouteBoard.IsSelected(selection, TreasureRoutes.Order[i]) ? tip + "\n" + module.T("panel.hunt.routes.last_tooltip") : tip;
        }

        var allName = module.T("panel.hunt.routes.all");
        menu.Preview = selection == TreasureRouteBoard.All ? allName : $"{count}/{TreasureRoutes.Order.Length}：{RouteNames(selection)}";
        menu.PreviewTip = module.T("panel.hunt.routes.tooltip").Replace("{routes}", RouteNames(selection));
        menu.AllLabel = $"{allName}##treasure-hunt-route-all";
        menu.AllTip = module.T("panel.hunt.routes.all_tooltip");
        menu.OnlyLabel = $"{module.T("panel.hunt.routes.chests_only")}##treasure-hunt-route-chests";
        menu.OnlyTip = module.T("panel.hunt.routes.chests_only_tooltip");
        menu.Other = module.T("panel.hunt.routes.other_chests").Replace("{count}", board.OtherRouteChests.ToString());
        menu.OtherTip = module.T(only ? "panel.hunt.routes.other_chests_skip_tooltip" : "panel.hunt.routes.other_chests_open_tooltip");
    }

    private static string Known(int value)
    {
        return value < 0 ? "?" : value.ToString();
    }

    private static string Clock(float seconds)
    {
        if (seconds < 0)
        {
            return "?";
        }

        var total = (int)MathF.Round(seconds);
        return $"{total / 60}:{total % 60:00}";
    }

    // 開始前就選得到；改了從下一次按開始生效，這一輪（含暫停後續跑）照開始時的方向。
    private void DrawDirection()
    {
        var config = module.Config;
        ImGui.TextColored(OcelotColor.Yellow, $"{I18N.T("hunter.direction.label")}:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 7);
        if (ImGui.BeginCombo("##treasure-hunt-direction", DirectionLabel(config.HuntDirection)))
        {
            foreach (var option in Directions)
            {
                if (ImGui.Selectable(DirectionLabel(option), option == config.HuntDirection) && option != config.HuntDirection)
                {
                    config.HuntDirection = option;
                    module.PluginConfig.Save();
                    var current = IsRunning || Progress.HasSaved ? $"；這一輪（含續跑）照原本的{(RoundReversed ? "逆向" : "正向")}" : string.Empty;
                    Svc.Log.Information($"[BOCCHI][寶箱巡迴] 方向改成「{DirectionLabel(option)}」（主視窗）：下一次按開始生效{current}");
                }
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(I18N.T("hunter.direction.tooltip"));
        }
    }

    private static string DirectionLabel(HuntDirection direction)
    {
        return I18N.T(direction switch
        {
            HuntDirection.Reverse => "hunter.direction.reverse",
            HuntDirection.Alternate => "hunter.direction.alternate",
            _ => "hunter.direction.forward",
        });
    }

    private void RefreshSummary(TreasureHuntMemory memory)
    {
        var template = module.T("panel.hunt.memory.chests_value");
        if (summaryVersion == memory.Version && ReferenceEquals(template, summaryChestTemplate))
        {
            return;
        }

        summaryVersion = memory.Version;
        summaryChestTemplate = template;
        summaryChests = template
            .Replace("{total}", memory.ChestCount.ToString())
            .Replace("{bronze}", memory.CountChests(TreasureType.Bronze).ToString())
            .Replace("{silver}", memory.CountChests(TreasureType.Silver).ToString());
        summaryVisited = module.T("panel.hunt.memory.visited_value").Replace("{count}", memory.VisitedCount.ToString());
    }

    private string TypeLabel(TreasureType type)
    {
        return module.T(type == TreasureType.Silver ? "panel.coffer.silver" : "panel.coffer.bronze");
    }
}
