using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using BOCCHI.Automation;
using BOCCHI.Chains;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules;
using BOCCHI.Modules.Pathfinder;
using BOCCHI.Modules.StateManager;
using BOCCHI.Windows;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Bindings.ImGui;
using Ocelot;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;
using Ocelot.IPC;
using Ocelot.Modules;
using TextCopy;

namespace BOCCHI.Pathfinding;

public abstract partial class Hunter
{
    // 每幀在送出規劃之前呼叫：true＝這一拍先不規劃（詠唱或讀圖還沒結束，起點會變）。
    private bool HoldPlanStart()
    {
        var player = Svc.Objects.LocalPlayer;
        var cast = player == null ? ReturnCastKind.None : ReturnCast.Classify(player.IsCasting, player.CastActionType, player.CastActionId);
        var condition = Svc.Condition;
        var wasWaiting = planGate.IsWaiting;
        var state = planGate.Step(Environment.TickCount64, cast, player != null, condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51],
            out var timedOut, out var waitedMs);
        if (state != PlanStartState.Ready)
        {
            if (!wasWaiting)
            {
                var what = state switch
                {
                    PlanStartState.Casting => cast == ReturnCastKind.Return ? "「返回」詠唱" : "詠唱",
                    PlanStartState.AfterReturn => "「返回」之後的讀圖",
                    _ => "讀圖",
                };
                Svc.Log.Information($"[BOCCHI][尋路] 「{AutomationControl.GetName(Kind)}」規劃前先等{what}結束再取起點（最多 {PlanStartGate.MaxWaitMs / 1000} 秒）；角色位置 {Player.Position:F1}");
            }

            return true;
        }

        if (wasWaiting)
        {
            Svc.Log.Information($"[BOCCHI][尋路] 「{AutomationControl.GetName(Kind)}」等了 {waitedMs / 1000f:F1} 秒{(timedOut ? "（到上限）" : "")}，從 {Player.Position:F1} 開始規劃");
        }

        return false;
    }

    // 有優先點才量：第一段要比步行與傳送。逆向沒有優先點時第一站是另一端，一樣先量；只巡部分路線時正向也量（候選＝全部要跑的點）。
    private StartLegSurvey? BeginStartLegSurvey(List<uint> valid, List<uint>? priority)
    {
        if (priority is not { Count: > 0 })
        {
            if (!roundReverse && HasRoundFilter && valid.Count > 0 && pathfinder is BasePathfinder)
            {
                var all = new List<(uint Node, Vector3 Position)>(valid.Count);
                foreach (var node in valid)
                {
                    if (TryGetNodePosition(node, out var nodeAt))
                    {
                        all.Add((node, nodeAt));
                    }
                }

                return all.Count == 0 ? null : StartLegSurvey.Begin(vnav, Player.Position, all, Environment.TickCount64);
            }

            if (!roundReverse || valid.Count == 0 || pathfinder is not BasePathfinder based)
            {
                return null;
            }

            var head = based.ReverseStartNode(Player.Position, valid);
            return TryGetNodePosition(head, out var at) ? StartLegSurvey.Begin(vnav, Player.Position, [(head, at)], Environment.TickCount64) : null;
        }

        var candidates = new List<(uint Node, Vector3 Position)>();
        foreach (var node in valid)
        {
            if (priority.Contains(node) && TryGetNodePosition(node, out var position))
            {
                candidates.Add((node, position));
            }
        }

        return candidates.Count == 0 ? null : StartLegSurvey.Begin(vnav, Player.Position, candidates, Environment.TickCount64);
    }

    // vnavmesh 一筆都沒回答時照原本的規劃（第一站＝直線最近的優先點、直接走過去）。
    private Task<List<PathfinderStep>> PlanPath(List<uint> valid, List<uint>? priority, StartLegSurvey? survey)
    {
        if (roundReverse && pathfinder is BasePathfinder based)
        {
            return PlanReversed(based, valid, priority ?? [], survey);
        }

        if (priority is not { Count: > 0 })
        {
            // 只有「只巡部分路線」才會在沒有優先點的正向量第一段；量不到的點用直線照樣比走法（同逆向）。
            if (survey != null && pathfinder is BasePathfinder routed)
            {
                return routed.FindPathFromStartLeg(survey.Start, valid, survey.Build(out _));
            }

            return pathfinder!.FindPath(Player.Position, valid);
        }

        if (survey == null)
        {
            return pathfinder!.FindPath(Player.Position, valid, priority);
        }

        var costs = survey.Build(out var answered);
        if (answered > 0)
        {
            return pathfinder!.FindPath(survey.Start, valid, priority, costs);
        }

        Svc.Log.Information($"[BOCCHI][尋路] 「{AutomationControl.GetName(Kind)}」第一段：vnavmesh 沒有回答任何步行路長（{survey.NotSent ?? $"{StartLegSurvey.TimeLimitMs / 1000} 秒內沒算完或都失敗"}），"
                            + "照原本的走法：直接走到直線最近的已記錄寶箱");
        return pathfinder!.FindPath(survey.Start, valid, priority);
    }

    // 有優先點時第一段照正向的規則（vnavmesh 沒回答就退回直線最近、直接步行）；沒有優先點時量不到也照直線比走法。
    private Task<List<PathfinderStep>> PlanReversed(BasePathfinder based, List<uint> valid, List<uint> priority, StartLegSurvey? survey)
    {
        var first = valid.Count(priority.Contains);
        StartLegCosts? costs = null;
        if (survey != null)
        {
            costs = survey.Build(out var answered);
            if (answered == 0 && first > 0)
            {
                costs = null;
                Svc.Log.Information($"[BOCCHI][尋路] 「{AutomationControl.GetName(Kind)}」第一段：vnavmesh 沒有回答任何步行路長（{survey.NotSent ?? $"{StartLegSurvey.TimeLimitMs / 1000} 秒內沒算完或都失敗"}），"
                                    + "照原本的走法：直接走到直線最近的已記錄寶箱");
            }
        }

        Svc.Log.Information($"[BOCCHI][尋路] 「{AutomationControl.GetName(Kind)}」這一輪逆向：已記錄的寶箱 {first} 個照正向的規則排，其餘 {valid.Count - first} 個點從正向規劃的另一端開始倒過來走（再照倒過來的方向做局部搜尋）");
        return based.FindPathReversed(survey?.Start ?? Player.Position, valid, priority, costs);
    }

    // 這一輪要跑的點。續跑時用現在的等級上限重新取點，再扣掉已經跑過的；TracksCoverage 的再交給 AdjustPlan（拿到的是不分路線的範圍）。
    private List<uint> PlanNodes()
    {
        var choice = ResolveMaxLevel();
        var inRange = GetValidNodes(choice.Level);
        var routed = HasRoundFilter ? inRange.FindAll(IsInRound) : inRange;
        var total = GetValidNodes(PathfinderConfig.LevelCeiling);
        SetRange(new HuntRange(choice.Level, choice.Source, routed.Count, HasRoundFilter ? total.Count(IsInRound) : total.Count));
        var valid = routed;
        if (resumePlan)
        {
            resumePlan = false;
            valid = progress.Remaining(routed);
            Svc.Log.Information($"[BOCCHI][巡迴進度] 續跑「{AutomationControl.GetName(Kind)}」重新規劃：範圍內 {routed.Count} 個點，略過已跑過的 {routed.Count - valid.Count} 個，"
                                + $"剩 {valid.Count} 個，從角色目前位置 {Player.Position:F1} 出發");
        }

        if (TracksCoverage)
        {
            planPriority ??= [];
            planPriority.Clear();
            if (ReferenceEquals(valid, inRange))
            {
                valid = new List<uint>(inRange);
            }

            AdjustPlan(inRange, valid, planPriority);
        }

        progress.AddPlanned(valid);
        return valid;
    }

    private void FinishEmptyRoute()
    {
        EndSegment(HuntEnd.Empty, null, HuntSegment.ProgressLabel(HuntEnd.Empty, false, false));
        Svc.Log.Information($"[BOCCHI][巡迴進度] 「{AutomationControl.GetName(Kind)}」沒有要跑的點（已跑 {ProgressText} 點；範圍內的點都跑過了，或區域等級上限內沒有點），已結束。");
        stopwatch.Stop();
        running = false;
        stepIndex = 0;
        Steps.Clear();
        pathfinder = null;
        routeEmpty = false;
        progress.Clear();
    }
}
