using BOCCHI.Windows;
using Dalamud.Bindings.ImGui;

namespace BOCCHI.Modules.StateManager;

public class Panel
{
    public bool Draw(StateManagerModule module)
    {
        if (!module.Config.ShowDebug)
        {
            return false;
        }

        MainPanelUi.Title(module.T("panel.title"));
        ImGui.SameLine();
        ImGui.TextUnformatted($"{module.T("panel.state.label")}: {module.GetStateText()}");

        return true;
    }
}
