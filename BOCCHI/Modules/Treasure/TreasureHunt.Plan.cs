using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BOCCHI.Automation;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.Pathfinder;
using BOCCHI.Pathfinding;
using BOCCHI.Windows;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.ImGuiMethods;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Ocelot;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;
using Ocelot.Modules;
using NativeTreasure = FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace BOCCHI.Modules.Treasure;

public partial class TreasureHunt
{
    protected override void AdjustPlan(List<uint> inRange, List<uint> planned, List<uint> priority)
    {
        var memory = module.HuntMemory;
        var chestsInRoutesOnly = roundChestsInRoutesOnly && HasRoundFilter;
        planRange.Clear();
        foreach (var node in inRange)
        {
            if (!chestsInRoutesOnly || IsInRound(node))
            {
                planRange.Add(node);
            }
        }

        var skipped = planned.RemoveAll(memory.IsCovered);
        planned.RemoveAll(memory.IsUnreachable);
        var senseSkipped = planned.RemoveAll(module.SenseSkips);
        if (senseSkipped > 0)
        {
            Svc.Log.Information($"{SenseTag} 魔尋寶感知數開滿（{module.SenseProgressText()}）：規劃時跳過 {senseSkipped} 個生成點（有已記錄寶箱的點照樣排）");
        }

        var added = 0;
        var outside = 0;
        var avoided = 0;
        var unreachable = 0;
        var otherRoute = 0;
        var fromOtherRoutes = 0;
        var chests = memory.Chests;
        for (var i = 0; i < chests.Count; i++)
        {
            var node = chests[i].Node;
            if (TreasureData.IsAvoided(node))
            {
                avoided++;
                continue;
            }

            if (node == 0 || !planRange.Contains(node))
            {
                if (node != 0 && inRange.Contains(node))
                {
                    otherRoute++;
                }
                else
                {
                    outside++;
                }

                continue;
            }

            if (memory.IsUnreachable(node))
            {
                unreachable++;
                continue;
            }

            if (!planned.Contains(node))
            {
                planned.Add(node);
                added++;
                if (HasRoundFilter && !IsInRound(node))
                {
                    fromOtherRoutes++;
                }
            }

            if (!priority.Contains(node))
            {
                priority.Add(node);
            }
        }

        if (HasRoundFilter)
        {
            Svc.Log.Information($"[BOCCHI][寶箱巡迴] 這一輪只巡路線「{RouteNames(RoundSelection)}」（{TreasureRoutes.Order.Length - roundExcluded.Count}/{TreasureRoutes.Order.Length} 段）："
                                + $"區域等級上限內 {inRange.Count(IsInRound)} 個點；其他路線的已記錄寶箱 "
                                + (chestsInRoutesOnly ? $"{otherRoute} 個不去開（只開所選路線內的）" : $"{fromOtherRoutes} 個照樣去開"));
        }

        if (skipped == 0 && priority.Count == 0 && outside == 0 && avoided == 0 && unreachable == 0)
        {
            return;
        }

        Svc.Log.Information($"[BOCCHI][寶箱記錄] 規劃寶箱巡迴：略過巡過的生成點 {skipped} 個；已記錄的寶箱 {priority.Count} 個一起排進這一輪"
                            + (added > 0 ? $"（其中 {added} 個原本不在要跑的點裡）" : string.Empty)
                            + (outside > 0 ? $"；另有 {outside} 個在區域等級上限外或對不上生成點，不去" : string.Empty)
                            + (avoided > 0 ? $"；{avoided} 個在刻意不去的生成點，只記錄不去開" : string.Empty)
                            + (unreachable > 0 ? $"；{unreachable} 個之前前往失敗，跳過到按清除或換區" : string.Empty)
                            + $"；這一輪共 {planned.Count} 個點");
    }

    protected override bool TryPickDetour(out uint node, out bool append)
    {
        node = 0;
        append = false;
        var memory = module.HuntMemory;
        if (memory.ChestCount == 0)
        {
            return false;
        }

        var player = Player.Position;
        var detour = TreasureDetourRules.Pick(Steps, stepIndex, player, GetDetectionRange(), memory, planRange, attempted);
        if (detour.Kind == TreasureDetourKind.None)
        {
            return false;
        }

        var chest = detour.Chest;
        node = chest.Node;
        append = detour.Kind == TreasureDetourKind.Append;
        attempted.Add(node);
        var position = chest.Position;
        var where = append ? "不在剩下的路線上，排到最後" : $"插在第 {stepIndex + 1} 步（{CurrentStep.Type}）前面，開完接回原本路線";
        Svc.Log.Information(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[BOCCHI][寶箱記錄] 去開已記錄的寶箱：{TypeLabel(chest.Type)} 生成點 {node} 位置 ({position.X:F1}, {position.Y:F1}, {position.Z:F1}) 距離 {detour.Distance:F1} 米；{where}"));
        return true;
    }

    protected override void OnNodeAbsent(uint node)
    {
        module.HuntMemory.NoteAbsent(node, Environment.TickCount64);
    }

    // 只有已記錄寶箱的點才跳過：沒有寶箱的點走不到照舊停止巡迴（多半是 vnavmesh 整個不能用，逐點跳過只會一路空轉）。
    protected override bool TrySkipUnreachableNode(uint node)
    {
        var memory = module.HuntMemory;
        if (!memory.HasChestAt(node))
        {
            return false;
        }

        memory.NoteUnreachable(node);
        attempted.Add(node);
        var chests = memory.Chests;
        for (var i = 0; i < chests.Count; i++)
        {
            if (chests[i].Node != node)
            {
                continue;
            }

            var position = chests[i].Position;
            Svc.Log.Information(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"[BOCCHI][寶箱記錄] 前往已記錄的寶箱失敗：{TypeLabel(chests[i].Type)} 生成點 {node} 位置 ({position.X:F1}, {position.Y:F1}, {position.Z:F1})；這個寶箱先跳過（照樣記錄），到按清除或換區前不再去開，巡迴繼續下一步"));
            break;
        }

        return true;
    }

    protected override void OnNodeStepDone(uint node)
    {
        attempted.Add(node);
    }
}
