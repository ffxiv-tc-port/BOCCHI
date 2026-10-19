using System;
using System.Numerics;
using BOCCHI.Data;
using BOCCHI.Windows;
using Dalamud.Bindings.ImGui;
using Ocelot;
using Ocelot.Ui;

namespace BOCCHI.Modules.Aggro;

/// <summary>主視窗面板（四列；走路穿越開著卻用不了時多一列）。組合字串只在數值或語言改變時重組，平常每幀不配置。</summary>
public class Panel
{
    private static readonly Vector4 Muted = new(0.6f, 0.6f, 0.6f, 1f);

    private static readonly Vector4 Warning = new(1f, 0.75f, 0.2f, 1f);

    private string? titleSource;

    private string titleText = string.Empty;

    private KnowledgeLevelReading shownKnowledge = new(-1, -1);

    private string? knowledgeSource;

    private string knowledgeText = string.Empty;

    private (int Matches, int Mismatches, bool Reliable) shownCalibration = (-1, -1, true);

    private string? calibrationSource;

    private string calibrationText = string.Empty;

    private int shownMinute = -2;

    private string? clockSource;

    private string clockText = string.Empty;

    private (int Certain, int Possible, int Skipped, int Dropped) shownCounts = (-1, -1, -1, -1);

    private string? countsSource;

    private string countsText = string.Empty;

    private (AvoidanceStatus Status, int Seconds) shownAvoidance = ((AvoidanceStatus)255, -1);

    private string? avoidanceSource;

    private string avoidanceText = string.Empty;

    private (int Rerouted, int Shortened, int Unavoidable, int Mitigated, int GaveWay) shownAvoidanceCounts = (-1, -1, -1, -1, -1);

    private string? avoidanceTooltipSource;

    private string avoidanceTooltip = string.Empty;

    public void Draw(AggroModule module)
    {
        var title = I18N.T("modules.aggro.panel.title");
        if (!ReferenceEquals(title, titleSource))
        {
            titleSource = title;
            titleText = title + ":";
        }

        // 四列：知見等級＋數量／等級來源＋時間／圖例／趕路迴避狀態。
        OcelotUi.Title(titleText);
        ImGui.SameLine();
        DrawKnowledge(module.Knowledge.Current);
        MainPanelUi.Gap();
        DrawCounts(module.Scanner.Snapshot);
        ImGui.Indent(16);
        DrawCalibration(module.Scanner.Calibration);
        MainPanelUi.Gap();
        DrawClock();
        DrawLegend();
        DrawAvoidance(module.Avoidance);
        DrawWalk(module);
        ImGui.Unindent(16);
    }

    // 只在走路穿越開著卻用不了時多一列：看得到「為什麼聽覺範圍還在繞路」。
    private static void DrawWalk(AggroModule module)
    {
        var config = module.Config;
        if (!config.AvoidWhileMoving || !config.WalkThroughSound)
        {
            return;
        }

        var walker = module.Walker;
        if (walker.KeyMissing)
        {
            ImGui.TextColored(Warning, I18N.T("modules.aggro.panel.avoid.walk_key_missing"));
        }
        else if (!walker.Toggle.Available(Environment.TickCount64) && !walker.Toggle.UserOverride)
        {
            ImGui.TextColored(Warning, I18N.T("modules.aggro.panel.avoid.walk_paused"));
        }
    }

    private void DrawAvoidance(AggroAvoidance avoidance)
    {
        var status = avoidance.Status;
        var seconds = status == AvoidanceStatus.Suppressed ? (int)Math.Max(0, (avoidance.SuppressedUntil - Environment.TickCount64 + 999) / 1000) : 0;
        var source = I18N.T(AvoidanceStatusKey(status));
        if ((status, seconds) != shownAvoidance || !ReferenceEquals(source, avoidanceSource))
        {
            shownAvoidance = (status, seconds);
            avoidanceSource = source;
            avoidanceText = source.Replace("{seconds}", seconds.ToString());
        }

        ImGui.TextColored(OcelotColor.Yellow, $"{I18N.T("modules.aggro.panel.avoid.label")}:");
        ImGui.SameLine();
        switch (status)
        {
            case AvoidanceStatus.Off:
            case AvoidanceStatus.Idle:
            case AvoidanceStatus.Combat:
                ImGui.TextColored(Muted, avoidanceText);
                break;
            case AvoidanceStatus.Unavoidable:
            case AvoidanceStatus.Suppressed:
            case AvoidanceStatus.EndpointMissing:
            case AvoidanceStatus.Holding:
            case AvoidanceStatus.Mitigated:
                ImGui.TextColored(Warning, avoidanceText);
                break;
            default:
                ImGui.TextUnformatted(avoidanceText);
                break;
        }

        if (!ImGui.IsItemHovered())
        {
            return;
        }

        var counts = (avoidance.ReroutedCount, avoidance.ShortenedCount, avoidance.UnavoidableCount, avoidance.MitigatedCount, avoidance.GaveWayCount);
        var tooltip = I18N.T("modules.aggro.panel.avoid.tooltip");
        if (counts != shownAvoidanceCounts || !ReferenceEquals(tooltip, avoidanceTooltipSource))
        {
            shownAvoidanceCounts = counts;
            avoidanceTooltipSource = tooltip;
            avoidanceTooltip = tooltip
                .Replace("{rerouted}", counts.ReroutedCount.ToString())
                .Replace("{shortened}", counts.ShortenedCount.ToString())
                .Replace("{unavoidable}", counts.UnavoidableCount.ToString())
                .Replace("{mitigated}", counts.MitigatedCount.ToString())
                .Replace("{gaveway}", counts.GaveWayCount.ToString());
        }

        ImGui.SetTooltip(avoidanceTooltip);
    }

    private static string AvoidanceStatusKey(AvoidanceStatus status)
    {
        return status switch
        {
            AvoidanceStatus.Off => "modules.aggro.panel.avoid.off",
            AvoidanceStatus.Combat => "modules.aggro.panel.avoid.combat",
            AvoidanceStatus.Watching => "modules.aggro.panel.avoid.watching",
            AvoidanceStatus.Planning => "modules.aggro.panel.avoid.planning",
            AvoidanceStatus.Rerouted => "modules.aggro.panel.avoid.rerouted",
            AvoidanceStatus.Unavoidable => "modules.aggro.panel.avoid.unavoidable",
            AvoidanceStatus.Suppressed => "modules.aggro.panel.avoid.suppressed",
            AvoidanceStatus.EndpointMissing => "modules.aggro.panel.avoid.endpoint_missing",
            AvoidanceStatus.Holding => "modules.aggro.panel.avoid.holding",
            AvoidanceStatus.Mitigated => "modules.aggro.panel.avoid.mitigated",
            _ => "modules.aggro.panel.avoid.idle",
        };
    }

    private void DrawKnowledge(KnowledgeLevelReading reading)
    {
        var source = I18N.T(reading.IsAvailable ? "modules.aggro.panel.knowledge.value" : "modules.aggro.panel.knowledge.unknown");
        if (reading != shownKnowledge || !ReferenceEquals(source, knowledgeSource))
        {
            shownKnowledge = reading;
            knowledgeSource = source;
            knowledgeText = source.Replace("{real}", reading.Real.ToString()).Replace("{synced}", reading.Synced.ToString());
        }

        if (reading.IsAvailable)
        {
            ImGui.TextUnformatted(knowledgeText);
            return;
        }

        ImGui.TextColored(Muted, knowledgeText);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(I18N.T("modules.aggro.panel.knowledge.unknown_tooltip"));
        }
    }

    private void DrawCalibration(LevelCalibration calibration)
    {
        var key = (calibration.Matches, calibration.Mismatches, calibration.Reliable);
        var source = I18N.T(calibration.Reliable ? "modules.aggro.panel.calibration.reliable" : "modules.aggro.panel.calibration.unreliable");
        if (key != shownCalibration || !ReferenceEquals(source, calibrationSource))
        {
            shownCalibration = key;
            calibrationSource = source;
            calibrationText = source
                .Replace("{matches}", calibration.Matches.ToString())
                .Replace("{mismatches}", calibration.Mismatches.ToString());
        }

        // 列上只放來源（不可靠時用警告色）；相符／不符的次數與說明在 tooltip。
        if (calibration.Reliable)
        {
            ImGui.TextUnformatted(I18N.T("modules.aggro.panel.calibration.reliable_short"));
        }
        else
        {
            ImGui.TextColored(Warning, I18N.T("modules.aggro.panel.calibration.unreliable_short"));
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"{calibrationText}\n{I18N.T("modules.aggro.panel.calibration.tooltip")}");
        }
    }

    private void DrawClock()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var minute = AggroJudge.EorzeaMinuteOfDay(now);
        var night = AggroJudge.UndeadNightAt(now) == UndeadNight.Night;
        var source = I18N.T(night ? "modules.aggro.panel.clock.night" : "modules.aggro.panel.clock.day");
        if (minute != shownMinute || !ReferenceEquals(source, clockSource))
        {
            shownMinute = minute;
            clockSource = source;
            clockText = source.Replace("{time}", $"{minute / 60:00}:{minute % 60:00}");
        }

        ImGui.TextUnformatted(clockText);
    }

    private void DrawCounts(AggroSnapshot snapshot)
    {
        var key = (snapshot.CertainCount, snapshot.PossibleCount, snapshot.SkippedCount, snapshot.DroppedCount);
        var source = I18N.T(key.DroppedCount > 0 ? "modules.aggro.panel.counts.with_dropped" : "modules.aggro.panel.counts.value");
        if (key != shownCounts || !ReferenceEquals(source, countsSource))
        {
            shownCounts = key;
            countsSource = source;
            countsText = source
                .Replace("{certain}", key.CertainCount.ToString())
                .Replace("{possible}", key.PossibleCount.ToString())
                .Replace("{skipped}", key.SkippedCount.ToString())
                .Replace("{dropped}", key.DroppedCount.ToString());
        }

        ImGui.TextUnformatted(countsText);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(I18N.T("modules.aggro.panel.counts.tooltip"));
        }
    }

    private static void DrawLegend()
    {
        Swatch(AggroRenderer.SightColor, I18N.T("modules.aggro.panel.legend.sight"));
        ImGui.SameLine();
        Swatch(AggroRenderer.SoundColor, I18N.T("modules.aggro.panel.legend.sound"));
        ImGui.SameLine();
        Swatch(AggroRenderer.ProximityColor, I18N.T("modules.aggro.panel.legend.proximity"));
        ImGui.SameLine();
        Swatch(AggroRenderer.MagicColor, I18N.T("modules.aggro.panel.legend.magic"));
        ImGui.SameLine();
        Swatch(AggroRenderer.BloodColor, I18N.T("modules.aggro.panel.legend.blood"));
        ImGui.SameLine();
        Swatch(AggroRenderer.UnknownColor, I18N.T("modules.aggro.panel.legend.unknown"));
        MainPanelUi.Gap();
        ImGui.TextColored(Muted, I18N.T("modules.aggro.panel.legend.states"));
    }

    private static void Swatch(Vector3 color, string text)
    {
        ImGui.TextColored(new Vector4(color, 1f), text);
    }
}
