using System.Linq;
using Dalamud.Bindings.ImGui;
using Ocelot;
using Ocelot.Ui;

namespace BOCCHI.Modules.MobFarmer;

public class Panel
{
    public void Draw(MobFarmerModule module)
    {
        OcelotUi.Title($"{I18N.T("modules.mob_farmer.panel.title")}:");
        OcelotUi.Indent(() =>
        {
            if (ImGui.Button(module.Farmer.Running ? I18N.T("generic.label.stop") : I18N.T("generic.label.start")))
            {
                module.Farmer.Toggle(module);
            }

            if (module.Farmer.Running)
            {
                OcelotUi.LabelledValue(I18N.T("modules.mob_farmer.panel.phase.label"), module.Farmer.StateMachine.State);
            }

            OcelotUi.LabelledValue(I18N.T("modules.mob_farmer.panel.not_engaged.label"), module.Scanner.NotInCombat.Count());
            OcelotUi.LabelledValue(I18N.T("modules.mob_farmer.panel.engaged.label"), module.Scanner.InCombat.Count());
        });
    }
}
