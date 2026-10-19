using System.Linq;
using BOCCHI.Modules.MobFarmer.States;
using BOCCHI.Windows;
using Dalamud.Bindings.ImGui;
using Ocelot;

namespace BOCCHI.Modules.MobFarmer;

/// <summary>一列：階段（執行中才有）與附近非交戰／交戰的數量。開始／停止在主視窗最上方的自動化控制。</summary>
public class Panel
{
    public void Draw(MobFarmerModule module)
    {
        MainPanelUi.Title(I18N.T("modules.mob_farmer.panel.title"));
        ImGui.SameLine();
        if (module.Farmer.Running)
        {
            MainPanelUi.Labelled(I18N.T("modules.mob_farmer.panel.phase.label"), I18N.T(PhaseKey(module.Farmer.StateMachine.State)));
            MainPanelUi.Gap();
        }

        module.Scanner.CountEngagement(out var notEngaged, out var engaged);
        MainPanelUi.Labelled(I18N.T("modules.mob_farmer.panel.not_engaged.label"), $"{notEngaged}");
        MainPanelUi.Gap();
        MainPanelUi.Labelled(I18N.T("modules.mob_farmer.panel.engaged.label"), $"{engaged}");
    }

    private static string PhaseKey(FarmerPhase phase)
    {
        return phase switch
        {
            FarmerPhase.Waiting => "modules.mob_farmer.panel.phase.waiting",
            FarmerPhase.Buffing => "modules.mob_farmer.panel.phase.buffing",
            FarmerPhase.Gathering => "modules.mob_farmer.panel.phase.gathering",
            FarmerPhase.Stacking => "modules.mob_farmer.panel.phase.stacking",
            _ => "modules.mob_farmer.panel.phase.fighting",
        };
    }
}
