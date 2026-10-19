using BOCCHI.Data;
using BOCCHI.Windows;
using Dalamud.Bindings.ImGui;
using Ocelot;

namespace BOCCHI.Modules.ForkedTower;

public class Panel
{
    /// <returns>有沒有畫東西；不在兩歧塔時 false，主視窗就不留空區段。</returns>
    public bool Draw(ForkedTowerModule module)
    {
        if (!ZoneData.IsInForkedTower())
        {
            return false;
        }

        MainPanelUi.Title(I18N.T("modules.forked_tower.panel.title"));
        ImGui.SameLine();
        ImGui.BeginGroup();
        MainPanelUi.Labelled(I18N.T("modules.forked_tower.panel.tower_id.label"), module.TowerRun.Hash);
        ImGui.EndGroup();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(I18N.T("modules.forked_tower.panel.tower_id.tooltip"));
        }

        return true;
    }
}
