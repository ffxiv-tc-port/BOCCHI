using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Lumina.Excel.Sheets;
using Ocelot.Ui;

namespace BOCCHI.Modules.Debug.Panels;

public class JobLevelPanel : Panel
{
    public override string GetName()
    {
        return "Job Level";
    }

    public override unsafe void Render(DebugModule module)
    {
        // var level = PublicContentOccultCrescent.GetState()->SupportJobLevels[1];
        var state = PublicContentOccultCrescent.GetState();
        OcelotUi.Indent(() =>
        {
            foreach (var job in Svc.Data.GetExcelSheet<MKDSupportJob>())
            {
                OcelotUi.Title(job.Unknown0.ToString());
                OcelotUi.Indent(() =>
                {
                    var level = state->SupportJobLevels[(byte)job.RowId];
                    OcelotUi.LabelledValue("Level", $"{level}/{job.Unknown10}");
                });

                OcelotUi.Indent(() =>
                {
                    var exp = state->SupportJobExperience[(byte)job.RowId];
                    OcelotUi.LabelledValue("Exp", exp);
                });
            }
        });
    }
}
