using BOCCHI.Windows;
using Dalamud.Bindings.ImGui;

namespace BOCCHI.Modules.Carrots;

public class Panel
{
    public void Draw(CarrotsModule module)
    {
        MainPanelUi.Title(module.T("panel.title"));
        if (module.carrots.Count <= 0)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(module.T("panel.none"));
            return;
        }

        ImGui.Indent(16);
        foreach (var carrot in module.carrots)
        {
            if (!carrot.IsValid())
            {
                continue;
            }

            var pos = carrot.GetPosition();
            ImGui.TextUnformatted($"{module.T("panel.label")}: ({pos.X:F2}, {pos.Y:F2}, {pos.Z:F2})");
        }

        ImGui.Unindent(16);
    }
}
