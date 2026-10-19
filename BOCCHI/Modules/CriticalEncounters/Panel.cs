using System;
using System.Linq;
using BOCCHI.Data;
using BOCCHI.Windows;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Dalamud.Bindings.ImGui;
using Ocelot;

namespace BOCCHI.Modules.CriticalEncounters;

/// <summary>每場兩列：名稱與狀態一列，尋路／傳送按鈕（只在準備中）與掉落圖示一列。兩歧塔資訊收成兩列。</summary>
public class Panel
{
    private readonly CeFarmPanel farm = new();

    public void Draw(CriticalEncountersModule module)
    {
        MainPanelUi.Title(module.T("panel.title"));
        var active = module.CriticalEncounters.Values.Count(ev => ev.State != DynamicEventState.Inactive);
        if (active <= 0)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(module.T("panel.none"));
        }
        else
        {
            ImGui.Indent(16);
            DrawEncounters(module);
            ImGui.Unindent(16);
        }

        if (module.Config.ShowHistory)
        {
            HistoryUi.Draw(module);
        }

        // 離線探針用未初始化的模組量繪製配置時沒有 Farm。
        if (module.Farm is not null)
        {
            farm.Draw(module);
        }
    }

    private static void DrawEncounters(CriticalEncountersModule module)
    {
        foreach (var ev in module.CriticalEncounters.Values)
        {
            if (!ZoneData.IsInOccultCrescent())
            {
                module.CriticalEncounters.Clear();
                return;
            }

            if (ev.EventType >= 4)
            {
                HandleTower(ev, module);
                continue;
            }

            if (ev.State == DynamicEventState.Inactive)
            {
                continue;
            }

            if (!EventData.CriticalEncounters.TryGetValue(ev.DynamicEventId, out var data))
            {
                continue;
            }

            ImGui.TextUnformatted(ev.Name);

            switch (ev.State)
            {
                case DynamicEventState.Register:
                    {
                        var start = DateTimeOffset.FromUnixTimeSeconds(ev.StartTimestamp).DateTime;
                        var timeUntilStart = start - DateTime.UtcNow;

                        ImGui.SameLine();
                        ImGui.TextUnformatted($"({module.T("panel.register")}: {timeUntilStart.Minutes:D2}:{timeUntilStart.Seconds:D2})");
                        break;
                    }
                case DynamicEventState.Warmup:
                    ImGui.SameLine();
                    ImGui.TextUnformatted($"({module.T("panel.warmup")})");
                    break;
                case DynamicEventState.Battle:
                    {
                        ImGui.SameLine();
                        ImGui.TextUnformatted($"({ev.Progress}%)");

                        if (module.Progress.TryGetValue(ev.DynamicEventId, out var progress))
                        {
                            var estimate = progress.EstimateTimeToCompletion();
                            if (estimate != null)
                            {
                                ImGui.SameLine();
                                ImGui.TextUnformatted($"({module.T("panel.estimated")} {estimate.Value:mm\\:ss})");
                            }
                        }

                        break;
                    }
                case DynamicEventState.Inactive:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            // 尋路／傳送按鈕只在準備中（還能報名）時畫。
            MainPanelUi.EventActions(module, ev.State == DynamicEventState.Register, data.Aethernet, ev.Position, ev.Name, $"ce_{ev.DynamicEventId}", data);
        }
    }

    private static void HandleTower(CriticalEncounterSnapshot ev, CriticalEncountersModule module)
    {
        if (!module.Config.TrackForkedTower || ev.State == DynamicEventState.Battle)
        {
            return;
        }

        var timer = module.Tracker.TowerTimer;
        var registering = ev.State != DynamicEventState.Inactive;
        var time = registering ? timer.GetTimeRemainingToRegister(ev.State) : timer.GetTimeToForkedTowerSpawn(ev.State);

        // 「開發中」縮成名稱旁的紅字標記，完整說明在 tooltip。
        ImGui.TextUnformatted(ev.Name);
        ImGui.SameLine();
        ImGui.TextColored(OcelotColor.Error, I18N.T("modules.critical_encounters.panel.tower.wip_short"));
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(I18N.T("modules.critical_encounters.panel.tower.wip"));
        }

        MainPanelUi.Gap();
        MainPanelUi.Labelled(I18N.T(registering ? "modules.critical_encounters.panel.tower.register" : "modules.critical_encounters.panel.tower.spawn_estimate"), $"{time:mm\\:ss}");

        ImGui.Indent(16);
        MainPanelUi.Labelled(I18N.T("modules.critical_encounters.panel.tower.critical_encounters_completed"), $"{timer.CriticalEncountersCompleted}");
        MainPanelUi.Gap();
        MainPanelUi.Labelled(I18N.T("modules.critical_encounters.panel.tower.fates_completed"), $"{timer.FatesCompleted}");

        if (TowerHelper.IsPlayerNearTower(TowerHelper.TowerType.Blood))
        {
            MainPanelUi.Labelled(I18N.T("modules.critical_encounters.panel.tower.players_on_platform"), $"{TowerHelper.GetPlayersInTowerZone(TowerHelper.TowerType.Blood)}");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(I18N.T("modules.critical_encounters.panel.tower.includes_you"));
            }

            MainPanelUi.Gap();
            MainPanelUi.Labelled(I18N.T("modules.critical_encounters.panel.tower.players_near_platform"), $"{TowerHelper.GetPlayersNearTowerZone(TowerHelper.TowerType.Blood)}");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(I18N.T("modules.critical_encounters.panel.tower.includes_you"));
            }
        }

        ImGui.Unindent(16);
    }
}
