using System;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace BOCCHI.Modules.CriticalEncounters;

/// <summary>
/// 緊急遭遇戰面板下方的「本次進島紀錄」，新的在上。每幀畫；文字一律走 ImU8String 內插，不產生字串。
/// 讀不到的一律畫成淡色「?」，不畫成 0；成敗判斷得出來（成功／失敗）畫實色，判不出來畫淡色。
/// </summary>
public static class HistoryUi
{
    public static void Draw(CriticalEncountersModule module)
    {
        var entries = module.History.Entries;
        if (!ImGui.CollapsingHeader($"{module.T("panel.history.title")} ({entries.Count})###bocchi_ce_history", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        ImGui.Indent(16);
        if (entries.Count == 0)
        {
            ImGui.TextDisabled(module.T("panel.history.empty"));
        }

        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            ImGui.BeginGroup();

            var seen = entry.FirstSeenUtc.ToLocalTime();
            if (entry.FirstSeenState == DynamicEventState.Register)
            {
                ImGui.TextUnformatted($"{seen:HH:mm}");
            }
            else
            {
                ImGui.TextDisabled($"{seen:HH:mm}");
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(entry.Name);
            ImGui.SameLine();
            DrawStatus(module, entry);

            ImGui.EndGroup();
            if (ImGui.IsItemHovered())
            {
                DrawTooltip(module, entry);
            }
        }

        ImGui.Unindent(16);
    }

    private static void DrawStatus(CriticalEncountersModule module, CriticalEncounterHistory.Entry entry)
    {
        if (entry.EndUtc is not { } end)
        {
            // 還開著，但追蹤器這一幀沒有它（離島、讀取中）：不知道後來怎麼了。
            if (!module.CriticalEncounters.ContainsKey(entry.Id))
            {
                ImGui.TextDisabled(module.T("panel.history.unknown"));
                return;
            }

            switch (entry.LastState)
            {
                case DynamicEventState.Register:
                    ImGui.TextUnformatted(module.T("panel.register"));
                    return;
                case DynamicEventState.Warmup:
                    ImGui.TextUnformatted(module.T("panel.warmup"));
                    return;
                default:
                    ImGui.TextUnformatted($"{module.T("panel.history.battle")}{entry.LastProgress}%");
                    return;
            }
        }

        if (entry.BattleStartUtc is not { } start)
        {
            if (entry.Unobserved)
            {
                ImGui.TextDisabled($"{module.T("panel.history.no_battle")}{module.T("panel.history.unobserved")}");
                return;
            }

            ImGui.TextUnformatted(module.T("panel.history.no_battle"));
            return;
        }

        var seconds = (long)Math.Max(0, (end - start).TotalSeconds);
        switch (entry.Outcome)
        {
            case CriticalEncounterOutcome.Success:
                ImGui.TextUnformatted(
                    $"{module.T("panel.history.completed")}{module.T("panel.history.duration_prefix")}{seconds / 60}:{seconds % 60:D2}{module.T("panel.history.duration_suffix")}");
                return;

            case CriticalEncounterOutcome.Failure:
                ImGui.TextUnformatted(
                    $"{module.T("panel.history.failed")}{entry.LastProgress}%{module.T("panel.history.duration_prefix")}{seconds / 60}:{seconds % 60:D2}{module.T("panel.history.duration_suffix")}");
                return;

            default:
                // 結束前一刻沒在更新（最後讀到的數字不一定是結束時的），或結束時只剩幾秒：成敗不明。
                ImGui.TextDisabled($"{module.T("panel.history.ended")}{entry.LastProgress}%{module.T(entry.EndedNearZero ? "panel.history.near_zero" : "panel.history.unobserved")}");
                return;
        }
    }

    private static void DrawTooltip(CriticalEncountersModule module, CriticalEncounterHistory.Entry entry)
    {
        ImGui.BeginTooltip();

        var seenState = entry.FirstSeenState == DynamicEventState.Register ? module.T("panel.history.tooltip.from_register") : module.T("panel.history.tooltip.joined_late");
        ImGui.TextUnformatted($"{module.T("panel.history.tooltip.first_seen")}{entry.FirstSeenUtc.ToLocalTime():HH:mm:ss}{seenState}");

        if (entry.BattleStartUtc is { } start)
        {
            ImGui.TextUnformatted($"{module.T("panel.history.tooltip.battle_start")}{start.ToLocalTime():HH:mm:ss}");
        }
        else
        {
            ImGui.TextDisabled($"{module.T("panel.history.tooltip.battle_start")}?");
        }

        if (entry.EndUtc is { } end)
        {
            ImGui.TextUnformatted($"{module.T("panel.history.tooltip.end")}{end.ToLocalTime():HH:mm:ss}");
        }
        else
        {
            ImGui.TextDisabled($"{module.T("panel.history.tooltip.end")}?");
        }

        if (entry.SawBattle)
        {
            var left = entry.LastSecondsLeft;
            ImGui.TextUnformatted($"{module.T("panel.history.tooltip.last_progress")}{entry.LastProgress}%");
            ImGui.TextUnformatted($"{module.T("panel.history.tooltip.seconds_left")}{left / 60}:{left % 60:D2}");
        }

        ImGui.TextUnformatted($"{module.T("panel.history.tooltip.participants")}{entry.MaxParticipants}");
        ImGui.TextUnformatted(module.T(entry.Participated ? "panel.history.tooltip.participated" : "panel.history.tooltip.not_participated"));
        if (entry.Unobserved)
        {
            ImGui.TextUnformatted(module.T("panel.history.tooltip.unobserved"));
        }

        ImGui.TextUnformatted(module.T("panel.history.tooltip.note"));
        ImGui.EndTooltip();
    }
}
