using System;
using System.Numerics;
using BOCCHI.Enums;
using Dalamud.Bindings.ImGui;
using Ocelot;

namespace BOCCHI.Modules.Treasure;

/// <summary>
/// 主視窗寶箱巡迴區「魔尋寶感知」那一列（純顯示，不觸發任何動作）：有效的數字＝實色、到上限用綠色；
/// 作廢的舊數字＝淡色並標明是上次的；沒有數字畫「?」。長說明放說明框。
/// 文字只在內容或語言變了才重組（說明框裡的「幾分鐘前」每分鐘一次）。只在框架執行緒用。
/// </summary>
internal sealed class TreasureSenseRow
{
    private static readonly Vector4 FullColor = new(0.45f, 0.85f, 0.45f, 1f);

    private TreasureSenseView view;

    private long minutes = -1;

    private int generation = -1;

    private string? template;

    private string bronze = string.Empty;

    private string silver = string.Empty;

    private string stale = string.Empty;

    private string tooltip = string.Empty;

    /// <param name="dim">淡色（作廢的舊數字與「?」）。</param>
    public void Draw(TreasureModule module, Vector4 dim)
    {
        Refresh(module);
        ImGui.TextColored(OcelotColor.Yellow, $"{module.T("panel.hunt.sense.label")}:");
        ImGui.SameLine();
        ImGui.BeginGroup();
        switch (view.State)
        {
            case TreasureSenseShow.Fresh:
                Segment(bronze, view.BronzeFull);
                ImGui.SameLine(0, ImGui.GetFontSize());
                Segment(silver, view.SilverFull);
                break;
            case TreasureSenseShow.Stale:
                ImGui.TextColored(dim, stale);
                break;
            default:
                ImGui.TextColored(dim, "?");
                break;
        }

        ImGui.EndGroup();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }
    }

    private static void Segment(string text, bool full)
    {
        if (full)
        {
            ImGui.TextColored(FullColor, text);
        }
        else
        {
            ImGui.TextUnformatted(text);
        }
    }

    private void Refresh(TreasureModule module)
    {
        var count = module.SenseCount;
        var memory = module.HuntMemory;
        var current = count.View(memory.OpenedCount(TreasureType.Silver), memory.OpenedCount(TreasureType.Bronze));
        var age = current.State == TreasureSenseShow.Unknown ? 0 : Math.Max(0, (Environment.TickCount64 - count.SensedAt) / 60_000);
        var bronzeTemplate = module.T("panel.hunt.sense.bronze");
        if (current == view && age == minutes && count.Generation == generation && ReferenceEquals(bronzeTemplate, template))
        {
            return;
        }

        view = current;
        minutes = age;
        generation = count.Generation;
        template = bronzeTemplate;
        bronze = bronzeTemplate.Replace("{count}", current.Bronze.ToString()).Replace("{cap}", TreasureSenseCount.BronzeCap.ToString());
        silver = module.T("panel.hunt.sense.silver").Replace("{count}", current.Silver.ToString()).Replace("{cap}", TreasureSenseCount.SilverCap.ToString());
        stale = module.T(current.Events > 0 ? "panel.hunt.sense.stale_events" : "panel.hunt.sense.stale_dropped")
            .Replace("{bronze}", bronze)
            .Replace("{silver}", silver)
            .Replace("{events}", current.Events.ToString());
        tooltip = module.T(current.State switch
            {
                TreasureSenseShow.Fresh => "panel.hunt.sense.tooltip",
                TreasureSenseShow.Stale => "panel.hunt.sense.stale_tooltip",
                _ => "panel.hunt.sense.unknown_tooltip",
            })
            .Replace("{minutes}", age.ToString())
            .Replace("{bronze_sensed}", count.SensedBronze.ToString())
            .Replace("{silver_sensed}", count.SensedSilver.ToString())
            .Replace("{bronze_opened}", Math.Max(0, count.SensedBronze - current.Bronze).ToString())
            .Replace("{silver_opened}", Math.Max(0, count.SensedSilver - current.Silver).ToString())
            .Replace("{events}", current.Events.ToString())
            .Replace("{bronze_cap}", TreasureSenseCount.BronzeCap.ToString())
            .Replace("{silver_cap}", TreasureSenseCount.SilverCap.ToString());
    }
}
