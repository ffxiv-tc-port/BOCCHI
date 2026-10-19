using BOCCHI.Windows;
using Dalamud.Interface;
using ECommons.ImGuiMethods;
using Dalamud.Bindings.ImGui;

namespace BOCCHI.Modules.Exp;

public class Panel
{
    public void Draw(ExpModule module)
    {
        MainPanelUi.Title(module.T("panel.title"), true);
        ImGui.SameLine();
        if (ImGuiEx.IconButton(FontAwesomeIcon.Redo, $"Reset##Exp"))
        {
            module.tracker.Reset();
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(module.T("panel.exp.label"));

        ImGui.SameLine();
        ImGui.TextUnformatted($"{module.tracker.GetExpPerHour():F2}");
    }
}
