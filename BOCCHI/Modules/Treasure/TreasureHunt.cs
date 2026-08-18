using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using BOCCHI.Data;
using BOCCHI.Pathfinding;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace BOCCHI.Modules.Treasure;

public class TreasureHunt(TreasureModule module) : Hunter(module)
{
    private List<TreasureData.TreasureDatum> Treasure = [];

    protected override IEnumerable<IGameObject> GetValidObjects()
    {
        return Svc.Objects
            .Where(o => o is
            {
                ObjectKind: ObjectKind.Treasure,
                IsDead: false,
                IsTargetable: true,
            } && o.IsValid());
    }

    protected override Vector3 GetDestinationForCurrentStep()
    {
        return Treasure.First(t => t.Id == CurrentStep.NodeId).Position;
    }

    protected override unsafe IPathfinder? CreatePathfinder()
    {
        Treasure.Clear();

        // 🔴 LayoutWorld 是 [StaticAddress(..., isPointer: true)],Instance() 回傳的是靜態槽
        //    「裡面的值」,可以合法為 null(區域切換、讀取畫面期間)。直接 -> 解參考產生的
        //    AccessViolationException 在 .NET Core 是 corrupted-state exception,try/catch 攔不到。
        var layoutWorld = LayoutWorld.Instance();
        if (layoutWorld == null)
        {
            Svc.Log.Warning("No layout world");
            return null;
        }

        var layout = layoutWorld->ActiveLayout;
        // 🔴 原本這裡(以及下面的 TryGetValue)只記了 warning 就往下走,等於檢查完照樣對 null
        //    解參考——半套邊界檢查。fail-closed:直接放棄這次建路,Hunter.Update() 下一拍會重試。
        if (layout == null)
        {
            Svc.Log.Warning("No active layout");
            return null;
        }

        if (!layout->InstancesByType.TryGetValue(InstanceType.Treasure, out var mapPtr, false))
        {
            Svc.Log.Warning("No active treasure map");
            return null;
        }

        foreach (ILayoutInstance* instance in mapPtr.Value->Values)
        {
            var transform = instance->GetTransformImpl();
            var position = transform->Translation;
            if (position.Y <= -10f)
            {
                continue;
            }

            var treasureRowId = Unsafe.Read<uint>((byte*)instance + 0x30);
            var sgbId = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Treasure>().GetRow(treasureRowId).SGB.RowId;
            if (sgbId != 1596 && sgbId != 1597)
            {
                continue;
            }

            Treasure.Add(new TreasureData.TreasureDatum(treasureRowId, position, sgbId));
        }

        Treasure = Treasure.OrderBy(t => t.Id).ToList();

        return new Pathfinder(Treasure, module.PluginConfig.PathfinderConfig.ReturnCost, module.PluginConfig.PathfinderConfig.TeleportCost);
    }

    protected override Func<Chain> GetInteractionChain(IGameObject obj)
    {
        return () => Chain.Create()
            .BreakIf(() => !GetValidObjects().Any(o => Vector3.Distance(o.Position, obj.Position) <= DISTANCE_TO_NODE_TO_USE))
            .Then(new TaskManagerTask(() =>
            {
                if (!EzThrottler.Throttle("ChestInteract", 250))
                {
                    return false;
                }

                if (Player.DistanceTo(obj) > DISTANCE_TO_NODE_TO_USE)
                {
                    return true;
                }

                unsafe
                {
                    Svc.Targets.Target = obj;
                    var gameObject = (GameObject*)(void*)obj.Address;
                    var instance = (FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure*)gameObject;
                    TargetSystem.Instance()->InteractWithObject(gameObject);
                    return instance->Flags.HasFlag(FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure.TreasureFlags.Opened);
                }
            }));
    }

    protected override List<uint> GetValidNodes(int max)
    {
        return TreasureData.Levels.Where(node => node.Value <= max).Select(node => node.Key).ToList();
    }
}
