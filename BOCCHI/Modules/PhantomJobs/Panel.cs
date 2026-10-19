using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Ocelot;

namespace BOCCHI.Modules.PhantomJobs;

/// <summary>主視窗的輔助職業等級總覽。每幀畫；文字一律走 ImU8String 內插，不產生字串。</summary>
/// <remarks>讀不到狀態時整塊只畫一行灰字，不畫成 0 級。</remarks>
public class Panel
{
    private static readonly Vector4 CurrentColor = new(0.55f, 0.8f, 1f, 1f);

    private static readonly Vector4 MaxedColor = new(0.42f, 0.85f, 0.45f, 1f);

    private readonly byte[] levels = new byte[PhantomJobTable.Count];

    private float pairWidth;

    public void Draw(PhantomJobsModule module)
    {
        ImGui.TextColored(OcelotColor.Yellow, $"{module.T("panel.title")}:");
        var current = PhantomJobTable.ReadLevels(levels);
        if (current < 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(module.T("panel.not_loaded"));
            return;
        }

        ImGui.Indent(16);
        try
        {
            DrawTable(module, current);
            DrawLevelingStatus(module, current);
        }
        finally
        {
            ImGui.Unindent(16);
        }
    }

    // 寬度夠放兩組「名稱＋等級」時一列排兩個職業；判斷用上一幀量到的最寬一組（0＝還沒量過）。
    private void DrawTable(PhantomJobsModule module, int current)
    {
        var pairs = pairWidth > 0 && ImGui.GetContentRegionAvail().X >= (pairWidth * 2) + (ImGui.GetStyle().ItemSpacing.X * 4) ? 2 : 1;
        if (!ImGui.BeginTable("##bocchi-phantom-job-levels", pairs * 2, ImGuiTableFlags.SizingFixedFit))
        {
            return;
        }

        var widest = 0f;
        var max = PhantomJobTable.MaxLevels;
        for (var i = 0; i < PhantomJobTable.Count; i++)
        {
            ImGui.TableNextColumn();
            var start = ImGui.GetCursorScreenPos().X;
            var name = PhantomJobTable.GetName(i);
            if (i == current)
            {
                ImGui.TextColored(CurrentColor, name);
            }
            else
            {
                ImGui.TextUnformatted(name);
            }

            ImGui.TableNextColumn();
            DrawLevel(module, i, levels[i], max[i], i == current);
            widest = Math.Max(widest, ImGui.GetItemRectMax().X - start);
        }

        ImGui.EndTable();
        pairWidth = widest;
    }

    private static void DrawLevel(PhantomJobsModule module, int id, byte level, byte max, bool isCurrent)
    {
        var mark = isCurrent ? module.T("panel.current") : string.Empty;
        if (level == 0)
        {
            ImGui.TextDisabled($"{module.T("panel.not_unlocked")}{mark}");
        }
        else if (!PhantomJobLevelingRules.IsLevelable(max))
        {
            ImGui.TextUnformatted($"Lv {level}{mark}");
        }
        else if (level >= max)
        {
            ImGui.TextColored(isCurrent ? CurrentColor : MaxedColor, $"Lv {level}/{max}{mark}");
        }
        else if (isCurrent)
        {
            ImGui.TextColored(CurrentColor, $"Lv {level}/{max}{mark}");
        }
        else
        {
            ImGui.TextUnformatted($"Lv {level}/{max}");
        }

        if (!ImGui.IsItemHovered())
        {
            return;
        }

        if (!PhantomJobLevelingRules.IsLevelable(max))
        {
            ImGui.SetTooltip(module.T("panel.tooltip.no_max"));
            return;
        }

        if (level > 0 && level < max && PhantomJobTable.ReadExperience(id) is { } exp)
        {
            ImGui.SetTooltip($"{module.T("panel.tooltip.experience")}{exp}");
        }
    }

    private void DrawLevelingStatus(PhantomJobsModule module, int current)
    {
        var leveling = module.Leveling;
        if (!leveling.IsRunning)
        {
            return;
        }

        var max = PhantomJobTable.MaxLevels;
        var next = PhantomJobLevelingRules.FindNext(levels, max, current);
        if (leveling.IsSwitching || next >= 0)
        {
            var target = leveling.IsSwitching ? leveling.NextTarget : next;
            ImGui.TextUnformatted($"{module.T("panel.leveling.switching")}{PhantomJobTable.GetName(target)}");
        }
        else if (PhantomJobLevelingRules.ShouldSwitchFrom(levels[current], max[current]))
        {
            ImGui.TextDisabled(module.T("panel.leveling.nothing"));
        }
        else
        {
            ImGui.TextDisabled(module.T("panel.leveling.waiting"));
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(module.T("panel.leveling.tooltip"));
        }
    }
}
