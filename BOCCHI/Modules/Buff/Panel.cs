using BOCCHI.Data;
using BOCCHI.Windows;
using Dalamud.Interface;
using ECommons.ImGuiMethods;
using Dalamud.Bindings.ImGui;

namespace BOCCHI.Modules.Buff;

public class Panel
{
    public void Draw(BuffModule module)
    {
        MainPanelUi.Title(module.T("panel.title"), true);
        ImGui.SameLine();

        var isNearKnowledgeCrystal = ZoneData.IsNearKnowledgeCrystal();
        var isQueued = module.BuffManager.IsQueued();

        if (ImGuiEx.IconButton(FontAwesomeIcon.Redo, "Button##ApplyBuffs", enabled: isNearKnowledgeCrystal && !isQueued))
        {
            module.BuffManager.QueueBuffs();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(module.T("panel.button.tooltip"));
        }
    }
}
