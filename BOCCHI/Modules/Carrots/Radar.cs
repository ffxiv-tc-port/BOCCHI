using BOCCHI.Data;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using Ocelot.Windows;

namespace BOCCHI.Modules.Carrots;

public class Radar
{
    public void Draw(RenderContext context, CarrotsModule module)
    {
        if (!ZoneData.IsInOccultCrescent() || Svc.Condition[ConditionFlag.InCombat])
        {
            return;
        }

        if (!module.Config.ShouldDrawLineToCarrots)
        {
            return;
        }

        if (Svc.Objects.LocalPlayer == null || Svc.Condition[ConditionFlag.InCombat])
        {
            return;
        }

        foreach (var carrot in module.carrots)
        {
            if (!carrot.IsValid())
            {
                continue;
            }

            context.DrawLine(carrot.GetPosition(), Carrot.Color);
        }
    }
}
