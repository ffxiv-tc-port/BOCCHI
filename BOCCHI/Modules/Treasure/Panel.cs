using System.Numerics;
using BOCCHI.Windows;
using ECommons.GameHelpers;
using Dalamud.Bindings.ImGui;

namespace BOCCHI.Modules.Treasure;

public class Panel
{
    public void Draw(TreasureModule module)
    {
        var treasures = module.Treasures;
        MainPanelUi.Title(module.T("panel.title"));
        if (treasures.Count <= 0)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(module.T("panel.none"));
        }

        ImGui.Indent(16);
        foreach (var treasure in treasures)
        {
            if (!treasure.IsValid())
            {
                continue;
            }

            var pos = treasure.GetPosition();
            ImGui.TextUnformatted($"{treasure.GetName()} ({pos.X:F2}, {pos.Y:F2}, {pos.Z:F2}) ({Vector3.Distance(Player.Position, pos)})");
        }

        ImGui.Unindent(16);
    }
}
