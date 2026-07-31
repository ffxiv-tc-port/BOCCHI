using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BOCCHI.Data;
using BOCCHI.Modules.Teleporter;
using ECommons.DalamudServices;
using Dalamud.Bindings.ImGui;
using Lumina.Data.Files;
using Lumina.Excel.Sheets;
using Ocelot.Ui;

namespace BOCCHI.Modules.Debug.Panels;

public class FatesPanel : Panel
{
    public Dictionary<uint, Vector3> FateLocations = [];

    public FatesPanel()
    {
        ProcessLgbData(Svc.ClientState.TerritoryType);
    }

    public void ProcessLgbData(ushort id)
    {
        if (id == 0)
        {
            return;
        }

        FateLocations.Clear();

        var territorySheet = Svc.Data.GetExcelSheet<TerritoryType>();
        var territoryRow = territorySheet?.GetRow(id);
        if (territoryRow == null)
        {
            Svc.Log.Error($"Could not load TerritoryType for ID {id}");
            return;
        }

        Dictionary<uint, uint> locations = [];
        var fateSheet = Svc.Data.GetExcelSheet<Fate>();
        foreach (var fate in EventData.Fates.Values)
        {
            // O(1) 查表，且查不到時跳過而不是對 default(Fate) 取 Location 丟 NullReferenceException
            if (fateSheet.TryGetRow(fate.Id, out var fateRow))
            {
                locations[fate.Id] = fateRow.Location;
            }
        }


        var bg = territoryRow?.Bg.ExtractText();
        var lgbFileName = "bg/" + bg![..(bg!.IndexOf("/level/", StringComparison.Ordinal) + 1)] + "level/planevent.lgb";
        var lgb = Svc.Data.GetFile<LgbFile>(lgbFileName);
        foreach (var layer in lgb?.Layers ?? [])
        {
            foreach (var instanceObject in layer.InstanceObjects)
            {
                if (locations.ContainsValue(instanceObject.InstanceId))
                {
                    var fateId = locations.First(kv => kv.Value == instanceObject.InstanceId).Key;
                    var transform = instanceObject.Transform;
                    var pos = transform.Translation;
                    FateLocations[fateId] = new Vector3(pos.X, pos.Y, pos.Z);
                }
            }
        }
    }

    public override string GetName()
    {
        return "Fates";
    }

    public override void Render(DebugModule module)
    {
        OcelotUi.Title("Fates:");
        OcelotUi.Indent(() =>
        {
            foreach (var data in EventData.Fates.Values)
            {
                ImGui.TextUnformatted(data.InternalName);

                if (module.TryGetModule<TeleporterModule>(out var teleporter) && teleporter!.IsReady())
                {
                    var start = FateLocations[data.Id];

                    teleporter.teleporter.Button(data.Aethernet, start, data.InternalName, $"fate_{data.Id}", data);
                }

                OcelotUi.Indent(() => EventIconRenderer.Drops(data, module.PluginConfig.EventDropConfig));

                if (data.Id != EventData.Fates.Keys.Max())
                {
                    OcelotUi.VSpace();
                }
            }
        });
    }

    public override void OnTerritoryChanged(ushort id, DebugModule module)
    {
        ProcessLgbData(id);
    }
}
