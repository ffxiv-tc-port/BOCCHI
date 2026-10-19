using BOCCHI.Windows;
using Dalamud.Interface;
using ECommons.ImGuiMethods;
using Dalamud.Bindings.ImGui;
using Ocelot;

namespace BOCCHI.Modules.Currency;

public class Panel
{
    public void Draw(CurrencyModule module)
    {
        // 標題放在表格第一欄，與銀幣那一列同列。
        if (!ImGui.BeginTable("CurrencyData##OCH", 4, ImGuiTableFlags.SizingFixedFit))
        {
            return;
        }

        // Silver
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        MainPanelUi.Title(module.T("panel.title"), true);

        ImGui.TableNextColumn();
        if (ImGuiEx.IconButton(FontAwesomeIcon.Redo, "Reset##Silver"))
        {
            module.Tracker.ResetSilver();
        }

        ImGui.TableNextColumn();
        ImGui.TextColored(OcelotColor.Yellow, module.T("panel.silver.label"));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{module.Tracker.GetSilverPerHour():F2}");

        // Gold
        ImGui.TableNextRow();

        ImGui.TableNextColumn();

        ImGui.TableNextColumn();
        if (ImGuiEx.IconButton(FontAwesomeIcon.Redo, "Reset##Gold"))
        {
            module.Tracker.ResetGold();
        }

        ImGui.TableNextColumn();
        ImGui.TextColored(OcelotColor.Yellow, module.T("panel.gold.label"));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{module.Tracker.GetGoldPerHour():F2}");

        ImGui.EndTable();
    }
}
