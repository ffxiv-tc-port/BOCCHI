using System;
using Dalamud.Bindings.ImGui;

namespace BOCCHI.Modules.Fates;

/// <summary>
/// F.A.T.E. 面板上的魔法甕列：進行中＝實色（親眼看到的）、下一個＝淡色（推估）、還沒看過＝淡色「?」。
/// 每幀畫；文字一律走 ImU8String 內插，不產生字串。
/// </summary>
public static class PotTimerUi
{
    public static void Draw(FatesModule module)
    {
        var pots = module.potCycle;
        var cycle = pots.Cycle;
        var now = DateTime.UtcNow;
        var period = module.Config.GetPotCyclePeriod();

        ImGui.Indent(16);

        var active = cycle.ActiveId;
        if (active != 0)
        {
            ImGui.TextUnformatted($"{module.T("panel.pot.active")}{pots.GetName(active)}");
            if (ImGui.IsItemHovered())
            {
                ActiveTooltip(module, cycle, active);
            }
        }

        if (cycle.TryPredict(now, period, out var nextId, out var nextUtc, out var missed))
        {
            var remaining = nextUtc - now;
            if (remaining >= TimeSpan.Zero)
            {
                var seconds = (long)Math.Ceiling(remaining.TotalSeconds);
                ImGui.TextDisabled(
                    $"{module.T("panel.pot.next")}{pots.GetName(nextId)} {module.T("panel.pot.in_prefix")}{seconds / 60:D2}:{seconds % 60:D2}{module.T("panel.pot.in_suffix")}{module.T("panel.pot.estimate")}");
            }
            else
            {
                var seconds = (long)Math.Floor(-remaining.TotalSeconds);
                ImGui.TextDisabled(
                    $"{module.T("panel.pot.next")}{pots.GetName(nextId)} {module.T("panel.pot.overdue")}{seconds / 60:D2}:{seconds % 60:D2}{module.T("panel.pot.estimate")}");
            }

            if (ImGui.IsItemHovered())
            {
                PredictionTooltip(module, pots, period, nextUtc, missed);
            }
        }
        else if (active == 0)
        {
            ImGui.TextDisabled($"{module.T("panel.pot.next")}?");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(module.T("panel.pot.tooltip.unknown"));
            }
        }

        ImGui.Unindent(16);
    }

    private static void ActiveTooltip(FatesModule module, PotCycle cycle, uint active)
    {
        ImGui.BeginTooltip();
        if (cycle.AnchorId == active)
        {
            var source = cycle.AnchorFromGameTime ? module.T("panel.pot.tooltip.source_game") : module.T("panel.pot.tooltip.source_seen");
            ImGui.TextUnformatted($"{module.T("panel.pot.tooltip.spawned_at")}{cycle.AnchorSpawnUtc.ToLocalTime():HH:mm:ss}{source}");
        }

        ImGui.TextUnformatted(module.T("panel.pot.tooltip.observed"));
        ImGui.EndTooltip();
    }

    private static void PredictionTooltip(FatesModule module, PotCycleTracker pots, TimeSpan period, DateTime nextUtc, int missed)
    {
        var cycle = pots.Cycle;
        ImGui.BeginTooltip();
        ImGui.TextUnformatted($"{module.T("panel.pot.tooltip.predicted_at")}{nextUtc.ToLocalTime():HH:mm:ss}");

        var source = cycle.AnchorFromGameTime ? module.T("panel.pot.tooltip.source_game") : module.T("panel.pot.tooltip.source_seen");
        ImGui.TextUnformatted($"{module.T("panel.pot.tooltip.anchor")}{pots.GetName(cycle.AnchorId)} {cycle.AnchorSpawnUtc.ToLocalTime():HH:mm:ss}{source}");
        ImGui.TextUnformatted($"{module.T("panel.pot.tooltip.period")}{(int)period.TotalMinutes}{module.T("panel.pot.tooltip.minutes")}");

        if (cycle.LastInterval is { } interval)
        {
            var seconds = (long)Math.Round(Math.Abs(interval.TotalSeconds));
            ImGui.TextUnformatted($"{module.T("panel.pot.tooltip.last_interval")}{seconds / 60}:{seconds % 60:D2}");
        }
        else
        {
            ImGui.TextDisabled($"{module.T("panel.pot.tooltip.last_interval")}?");
        }

        if (missed > 0)
        {
            ImGui.TextUnformatted($"{module.T("panel.pot.tooltip.missed")}{missed}");
        }

        ImGui.TextUnformatted(module.T("panel.pot.tooltip.note"));
        ImGui.EndTooltip();
    }
}
