using BOCCHI.Data;
using Dalamud.Bindings.ImGui;
using Ocelot.Ui;
using Ocelot;

namespace BOCCHI.Modules.ForkedTower;

public class Panel
{
    public void Draw(ForkedTowerModule module)
    {
        if (!ZoneData.IsInForkedTower())
        {
            return;
        }

        OcelotUi.Title($"{I18N.T("modules.forked_tower.panel.title")}:");
        OcelotUi.Indent(() =>
        {
            var state = OcelotUi.LabelledValue(I18N.T("modules.forked_tower.panel.tower_id.label"), module.TowerRun.Hash);
            if (state == UiState.Hovered)
            {
                ImGui.SetTooltip(I18N.T("modules.forked_tower.panel.tower_id.tooltip"));
            }
        });
    }
}
