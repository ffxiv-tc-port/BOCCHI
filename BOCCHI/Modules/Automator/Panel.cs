using System;
using BOCCHI.Windows;
using Dalamud.Bindings.ImGui;
using Ocelot.Ui;

namespace BOCCHI.Modules.Automator;

public class Panel
{
    /// <summary>非法模式檢視器視窗用（三列）。</summary>
    public void Draw(AutomatorModule module)
    {
        OcelotUi.Title($"{module.T("panel.title")}:");
        OcelotUi.Indent(() =>
        {
            OcelotUi.Title($"{module.T("panel.activity.label")}:");
            try
            {
                var name = module.automator.Activity?.GetName() ?? module.T("panel.activity.none");
                ImGui.SameLine();
                ImGui.TextUnformatted(name);
            }
            catch (AccessViolationException)
            {
                return;
            }

            OcelotUi.Title($"{module.T("panel.activity_state.label")}:");
            ImGui.SameLine();
            ImGui.TextUnformatted(module.automator.Activity?.state.ToLabel() ?? module.T("panel.activity_state.none"));
        });
    }

    /// <summary>主視窗最上方自動化控制用（一列），內容與 <see cref="Draw"/> 相同。</summary>
    public void DrawCompact(AutomatorModule module)
    {
        string name;
        try
        {
            name = module.automator.Activity?.GetName() ?? module.T("panel.activity.none");
        }
        catch (AccessViolationException)
        {
            return;
        }

        MainPanelUi.Title(module.T("panel.title"));
        ImGui.SameLine();
        MainPanelUi.Labelled(module.T("panel.activity.label"), name);
        MainPanelUi.Gap();
        MainPanelUi.Labelled(module.T("panel.activity_state.label"), module.automator.Activity?.state.ToLabel() ?? module.T("panel.activity_state.none"));
    }
}
