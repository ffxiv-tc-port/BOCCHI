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
    public void Update()
    {
        if (progress.State == HuntProgressState.Paused && progress.Expire(Environment.TickCount64, ResumeExpiryMs))
        {
            Svc.Log.Information($"[BOCCHI][巡迴進度] 進度失效「{AutomationControl.GetName(Kind)}」（暫停超過 {config.ResumeExpiryMinutes} 分鐘）：已跑 {ProgressText} 點；"
                                + "續跑停用，要再跑請按開始從頭跑");
        }

        if (!running || Plugin.Chain.IsRunning)
        {
            return;
        }

        if (IsStalled())
        {
            if (SkipUnreachableStep($"超過 {StepStallLimitMs / 1000} 秒沒完成"))
            {
                return;
            }

            var reason = I18N.T("automation.reason.stalled", new Dictionary<string, string>
            {
                ["step"] = (stepIndex + 1).ToString(),
                ["seconds"] = (StepStallLimitMs / 1000).ToString(),
            });
            plugin.Automation.StopKeepingProgress(Kind, reason, true);
            return;
        }

        if (routeEmpty)
        {
            FinishEmptyRoute();
            return;
        }

        if (pathfinder == null && Steps.Count <= 0)
        {
            pathfinder = CreatePathfinder();
        }

        MaintainWatcherChain();
    }

    private void MaintainWatcherChain()
    {
        if (Plugin.Chain.IsRunning)
        {
            return;
        }

        if (pathfinder is { State: PathfinderState.LoadFailed })
        {
            // 不重建規劃器重讀：同一個檔每一拍都會再失敗一次。
            pathfinder = null;
            Svc.Log.Information($"[BOCCHI][尋路] 「{AutomationControl.GetName(Kind)}」讀不到尋路成本表（原因見上一行 Error），停止巡迴");
            plugin.Automation.StopKeepingProgress(Kind, I18N.T("automation.reason.data_load_failed"), true);
            return;
        }

        if (pathfinder != null && pathfinder.State != PathfinderState.PathfindingDone)
        {
            if (HoldPlanStart() || HoldBeforePlan())
            {
                return;
            }

            Plugin.Chain.Submit(() =>
            {
                Task<List<PathfinderStep>> steps = null!;
                StartLegSurvey? survey = null;
                var valid = PlanNodes();
                var priority = planPriority;
                if (valid.Count == 0)
                {
                    return Chain.Create("Hunter.Pathfinding").Then(_ =>
                    {
                        pathfinder = null;
                        routeEmpty = true;
                    });
                }

                // Prep pathfinding
                return Chain.Create("Hunter.Pathfinding")
                    .Then(new TaskManagerTask(() => pathfinder?.State is PathfinderState.FileLoaded or PathfinderState.LoadFailed))
                    .BreakIf(() => pathfinder?.State != PathfinderState.FileLoaded)
                    .Then(_ => survey = BeginStartLegSurvey(valid, priority))
                    .Then(_ => survey == null || survey.IsDone(Environment.TickCount64))
                    .Then(_ => steps = PlanPath(valid, priority, survey))
                    .Then(new TaskManagerTask(() => steps!.IsCompleted))
                    .Then(_ => Steps = steps!.Result)
                    .Then(_ =>
                    {
                        var options = new JsonSerializerOptions
                        {
                            WriteIndented = true,
                            Converters =
                            {
                                new PathfinderStepConverter(),
                            },
                        };

                        JSON = JsonSerializer.Serialize(Steps, options);
                    })
                    .Then(_ => pathfinder = null);
            });

            return;
        }

        if (StepProcessor.IsRunning || StepProcessor.QueueCount > 0)
        {
            return;
        }

        progress.CommitReached();

        if (stepIndex >= Steps.Count)
        {
            Teardown();
            return;
        }

        var now = Environment.TickCount64;
        ObserveCombat(now);
        ObserveManualMovement(now);
        if (now < nextStepTickAt)
        {
            return;
        }

        nextStepTickAt = now + StepTickIntervalMs;

        var obj = GetValidObjects().FirstOrDefault(o => Vector3.Distance(Player.Position, o.Position) <= 5f);
        if (obj != null)
        {
            StepProcessor.Submit(GetInteractionChain(obj));
        }

        RunCurrentStep();
    }

    private void RunCurrentStep()
    {
        try
        {
            SkipCoveredSteps();
            if (stepIndex >= Steps.Count)
            {
                return;
            }

            ApplyDetour();
            ApplyCarrotDetour();

            // 戰鬥中原地等：不跑步驟（不送導航、不叫坐騎、不詠唱「返回」），5 米內的寶箱照樣開。
            if (combatWait.Holding)
            {
                return;
            }

            var step = CurrentStep;
            startedStep = stepIndex;
            if (Handlers[step.Type]())
            {
                if (step.Type == PathfinderStepType.WalkToNode)
                {
                    // 有送出互動（或上坐騎）就等動作鏈跑完才算跑過，沒有就當場算。
                    progress.Reach(step.NodeId);
                    if (!StepProcessor.IsRunning && StepProcessor.QueueCount <= 0)
                    {
                        progress.CommitReached();
                    }

                    OnNodeStepDone(step.NodeId);
                }

                stepIndex++;
            }
        }
        catch (Exception ex)
        {
            if (stepErrorIndex == stepIndex && stepErrorType == ex.GetType())
            {
                Svc.Log.Debug($"[BOCCHI][自動化] 「{AutomationControl.GetName(Kind)}」第 {stepIndex + 1} 步又擲出 {ex.GetType().Name}：{ex.Message}");
            }
            else
            {
                stepErrorIndex = stepIndex;
                stepErrorType = ex.GetType();
                Svc.Log.Error(ex, $"[BOCCHI][自動化] 「{AutomationControl.GetName(Kind)}」第 {stepIndex + 1} 步處理時擲出例外，下一輪再試（同一步同一種例外之後只寫 Debug）。");
            }
        }
    }

    // 跳過已確認的點（規則見 HuntStepRules.TrySkip）；正在走向被跳過的點時先停下導航。
    private void SkipCoveredSteps()
    {
        if (!TracksCoverage)
        {
            return;
        }

        coveredPredicate ??= IsNodeCovered;
        while (HuntStepRules.TrySkip(Steps, stepIndex, startedStep == stepIndex, coveredPredicate, out var next, out var node))
        {
            if (startedStep == stepIndex)
            {
                StopNavigationForNewTarget();
            }

            Svc.Log.Debug($"[BOCCHI][巡迴進度] 「{AutomationControl.GetName(Kind)}」略過已確認的點 {node}（第 {stepIndex + 1}～{next} 步）");
            progress.Reach(node);
            progress.CommitReached();
            stepIndex = next;
            startedStep = -1;
        }
    }

    private void ApplyDetour()
    {
        if (!TryPickDetour(out var node, out var append))
        {
            return;
        }

        if (append)
        {
            Steps.Add(PathfinderStep.WalkToDestination(node));
            return;
        }

        Steps.Insert(stepIndex, PathfinderStep.WalkToDestination(node));
        watchedStep = -1;
        startedStep = -1;
        StopNavigationForNewTarget();
    }

    private void ApplyCarrotDetour()
    {
        if (CurrentStep.Type is not (PathfinderStepType.WalkToNode or PathfinderStepType.WalkToAethernet) || yieldGate.Holding)
        {
            return;
        }

        if (!TryPickCarrot(out var objectId, out var position))
        {
            return;
        }

        Steps.Insert(stepIndex, PathfinderStep.DigCarrot(objectId, position));
        watchedStep = -1;
        startedStep = -1;
        ledgeClimb.Reset();
        StopNavigationForNewTarget();
    }

    // 挖胡蘿蔔那一步結束：移除它，原本那一步回到目前這一步、重新開始計時與送移動。
    private void RemoveCarrotStep()
    {
        if (stepIndex < Steps.Count && Steps[stepIndex].Type == PathfinderStepType.DigCarrot)
        {
            Steps.RemoveAt(stepIndex);
        }

        watchedStep = -1;
        startedStep = -1;
        StopNavigationForNewTarget();
    }

    // 前往失敗的那一步交給子類別決定要不要跳過；跳過就停下導航與這一步的動作鏈、換下一步，巡迴不停止。
    private bool SkipUnreachableStep(string what)
    {
        if (stepIndex >= Steps.Count)
        {
            return false;
        }

        var step = CurrentStep;
        if (step.Type == PathfinderStepType.DigCarrot)
        {
            StepProcessor.Abort();
            RemoveCarrotStep();
            OnCarrotUnreachable(step, what);
            return true;
        }

        if (step.Type != PathfinderStepType.WalkToNode || !TrySkipUnreachableNode(step.NodeId))
        {
            return false;
        }

        // 先換到下一步再碰 vnavmesh：停導航擲例外時也不會每一拍重跳同一步。
        var skipped = stepIndex;
        stepIndex++;
        startedStep = -1;
        watchedStep = -1;
        Svc.Log.Information($"[BOCCHI][尋路] 「{AutomationControl.GetName(Kind)}」第 {skipped + 1} 步（走向點 {step.NodeId}）{what}：跳過這一步，巡迴繼續。角色位置 {Player.Position:F1}");
        StepProcessor.Abort();
        StopNavigationForNewTarget();
        return true;
    }

    // 換目的地：先取消還在算的尋路再停下（算完的舊路會把角色帶回原本的目標），守衛重新計時。
    private void StopNavigationForNewTarget()
    {
        staleMovePending = OwnPathfind.CancelIfOnlyOwn(vnav, OwnMoveDestinations.HasCurrent(TravelIntent.State)) is OwnPathfindCancel.OthersQueued or OwnPathfindCancel.Unreadable;
        StopOwnPath("換目的地", newTargetNotice);
        moveGuard.Reset();
    }

    // Lifestream 帶角色走時看到 vnavmesh 在走就中止全部任務。讓位時絕不取消算路（全域的，會連 Lifestream 在算的一起取消）：
    // 只停已經在走的 BOCCHI 自己的路；BOCCHI 還在算的路算完開走時，讓位期間每拍再停（ObserveManualMovement）。回傳有沒有停下東西。
    private bool StopNavigationForLifestream()
    {
        var stopped = OwnPathStop.Stops(StopOwnPath("為 Lifestream 讓位", lifestreamNotice));
        moveGuard.Reset();
        return stopped;
    }

    // 只停 BOCCHI 自己的路線：自己的目的地＝最近送出的、還沒作廢的趕路登記，再加上最後一次送出的目的地（換步之後目前這一步已經不是在走的那條）。
    // Own／Unknown 停；別人的路線不停，同一處 ForeignPathNotice.IntervalMs 內寫一行 Information。回傳判定。
    private OwnPathVerdict StopOwnPath(string when, ForeignPathNotice notice)
    {
        stopDestinations.CaptureCurrent(TravelIntent.State);
        if (lastSentDestination is { } sent)
        {
            stopDestinations.Add(sent);
        }

        var verdict = OwnPathStop.StopIfOwn(vnav, stopDestinations, out var end);
        if (verdict == OwnPathVerdict.Foreign && notice.Due(Environment.TickCount64))
        {
            Svc.Log.Information($"[BOCCHI][尋路] 「{AutomationControl.GetName(Kind)}」{when}時 vnavmesh 正在走別人的路線（終點離 BOCCHI 送過的目的地 {stopDestinations.NearestXZ(end!.Value):F1} 米）："
                                + "不停，交給送出它的插件或你自己");
        }

        return verdict;
    }

    private bool IsStalled()
    {
        if (Steps.Count <= 0 || stepIndex >= Steps.Count)
        {
            watchedStep = -1;
            return false;
        }

        var now = Environment.TickCount64;
        if (stepIndex != watchedStep)
        {
            watchedStep = stepIndex;
            watchedStepSince = now;
            return false;
        }

        return now - watchedStepSince > StepStallLimitMs;
    }
}
