using System;
using BOCCHI.Data;
using BOCCHI.Windows;
using Dalamud.Bindings.ImGui;
using Ocelot.Ui;

namespace BOCCHI.Modules.Fates;

/// <summary>每場兩列：名稱與進度一列，尋路／傳送按鈕與掉落圖示一列。</summary>
public class Panel
{
    public void Draw(FatesModule module)
    {
        MainPanelUi.Title(module.T("panel.title"));
        var none = module.tracker.Fates.Count <= 0;
        if (none)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(module.T("panel.none"));
        }

        if (module.Config.ShowPotTimer)
        {
            PotTimerUi.Draw(module);
        }

        if (none)
        {
            return;
        }

        ImGui.Indent(16);
        var count = module.fates.Count;
        var index = 0;
        foreach (var fate in module.fates.Values)
        {
            index++;
            if (!ZoneData.IsInOccultCrescent())
            {
                module.fates.Clear();
                break;
            }

            try
            {
                ImGui.TextUnformatted($"{fate.Name} ({fate.CurrentProgress}%)");
            }
            catch (AccessViolationException)
            {
                continue;
            }

            var estimate = fate.Progress.EstimateTimeToCompletion();
            if (estimate != null)
            {
                ImGui.SameLine();
                ImGui.TextUnformatted($"({module.T("panel.estimated")} {estimate.Value:mm\\:ss})");
            }

            MainPanelUi.EventActions(module, true, fate.Data.Aethernet, fate.StartPosition, fate.Name, $"fate_{fate.Id}", fate.Data);

            if (index < count)
            {
                OcelotUi.VSpace(4);
            }
        }

        ImGui.Unindent(16);
    }
}
