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
    /// <summary>
    /// 走向 <paramref name="destination"/>：在走或還在算路時不重送（見 <see cref="MoveRequestGuard"/>）。
    /// 回 false＝同一個目的地太久都沒開始走：子類別讓這一點跳過時已經換到下一步，否則已寫記錄並停止巡迴（進度保留成暫停）；
    /// 呼叫端都要立刻 return。
    /// </summary>
    private bool RequestMove(Vector3 destination, TravelSource? source)
    {
        var moving = vnav.IsRunning();
        var computing = vnav.IsSimpleMoveInProgress();
        if (staleMovePending && (moving || !computing))
        {
            staleMovePending = false;
            if (moving)
            {
                vnav.Stop();
                moving = false;
                moveGuard.Reset();
                Svc.Log.Debug($"[BOCCHI][尋路] 「{AutomationControl.GetName(Kind)}」換目的地前沒取消掉的舊算路算完開走了：停下，改送 {destination:F1}");
            }
        }

        holdingForManual = false;
        switch (yieldGate.Next(moving || computing))
        {
            case HuntMoveDecision.Hold:
                holdingForManual = true;
                return true;
            case HuntMoveDecision.Resume:
                moveGuard.Reset();
                ReportYield(ref resumeReported, $"[BOCCHI][巡迴讓位] 「{AutomationControl.GetName(Kind)}」第 {stepIndex + 1} 步角色已靜止 {ManualMoveDetector.ResumeQuietMs / 1000f:F1} 秒："
                                                + $"從目前位置 {Player.Position:F1} 接續前往 {destination:F1}（這一段累計讓位 {segmentYieldMs / 1000f:F1} 秒）");
                break;
        }

        var decision = moveGuard.Next(destination, Player.Position, moving, computing, vnav.IsNavmeshReady(), Environment.TickCount64);
        if (decision == MoveRequestDecision.Send)
        {
            if (source is { } travel)
            {
                TravelIntent.Register(destination, travel);
            }
            else
            {
                // 沒登記趕路的移動（戰鬥中跑回營地）也記下，停止後的導航守衛才認得是自己的。
                OwnMoveDestinations.Note(destination);
            }

            lastSentDestination = destination;
            vnav.PathfindAndMoveTo(destination, false);
            return true;
        }

        if (decision != MoveRequestDecision.GiveUp)
        {
            return true;
        }

        var seconds = MoveRequestGuard.NoStartLimitMs / 1000;
        if (SkipUnreachableStep($"走向 {destination:F1}：{seconds} 秒內都沒開始移動（送了 {moveGuard.Sends} 次）"))
        {
            return false;
        }

        Svc.Log.Information($"[BOCCHI][尋路] 「{AutomationControl.GetName(Kind)}」第 {stepIndex + 1} 步（{CurrentStep.Type}）走向 {destination:F1}：{seconds} 秒內都沒開始移動"
                            + $"（vnavmesh 算不出路或一直失敗，這段期間送了 {moveGuard.Sends} 次），不再重送，停止巡迴。角色位置 {Player.Position:F1}");
        var reason = I18N.T("automation.reason.no_path", new Dictionary<string, string>
        {
            ["step"] = (stepIndex + 1).ToString(),
            ["seconds"] = seconds.ToString(),
        });
        plugin.Automation.StopKeepingProgress(Kind, reason, true);
        return false;
    }

    protected bool WalkToNodeHandler()
    {
        var destination = GetDestinationForCurrentStep();
        if (TryGetLedge(CurrentStep.NodeId, out var ledge) && ClimbLedge(destination, ledge) is { } climbed)
        {
            return climbed;
        }

        if (!RequestMove(destination, TravelSource.HuntNode))
        {
            return false;
        }

        if (!Player.Mounted && !holdingForManual)
        {
            StepProcessor.SubmitFront(ChainHelper.MountChain());
        }

        distance = Player.DistanceTo(destination);
        if (distance <= GetDetectionRange())
        {
            var obj = GetValidObjects().FirstOrDefault(o => Vector3.Distance(destination, o.Position) <= 5f);

            if (obj == null)
            {
                vnav.Stop();
                OnNodeAbsent(CurrentStep.NodeId);
                return true;
            }

            if (distance <= DISTANCE_TO_NODE_TO_USE)
            {
                StepProcessor.SubmitFront(GetInteractionChain(obj));
                return true;
            }
        }

        return distance <= DISTANCE_TO_NODE_TO_USE;
    }

    // 跳上低台的那幾拍（規則見 LedgeClimb）；回 null＝站在台上了，照常走到點。附近確定沒有寶箱就照常判「那裡沒有」，不跳。
    private bool? ClimbLedge(Vector3 destination, HuntLedge ledge)
    {
        var player = Player.Position;
        var node = CurrentStep.NodeId;
        distance = Vector3.Distance(player, destination);
        var onTop = ledge.IsOnTop(player, destination);
        if (!onTop && distance <= GetDetectionRange() && !GetValidObjects().Any(o => Vector3.Distance(destination, o.Position) <= 5f))
        {
            ledgeClimb.Reset();
            vnav.Stop();
            OnNodeAbsent(node);
            return true;
        }

        var now = Environment.TickCount64;
        var action = ledgeClimb.Step(now, stepIndex, player, onTop, Svc.Condition[ConditionFlag.Jumping], yieldGate.Holding, ledge);
        if (action == LedgeAction.Jump)
        {
            var sent = TryJump();
            Svc.Log.Debug($"[BOCCHI][跳上低台] 「{AutomationControl.GetName(Kind)}」點 {node} 第 {ledgeClimb.Attempts + 1} 次：{(sent ? "跳" : "跳躍送不出去")}；角色位置 {player:F2}");
            action = ledgeClimb.JumpSent(sent, now);
        }

        switch (action)
        {
            case LedgeAction.Proceed:
                return null;
            case LedgeAction.Approach:
                if (!RequestMove(ledge.Approach, TravelSource.HuntNode))
                {
                    return false;
                }

                if (!Player.Mounted && !holdingForManual)
                {
                    StepProcessor.SubmitFront(ChainHelper.MountChain());
                }

                return false;
            case LedgeAction.Stop:
                StopNavigationForNewTarget();
                if (ledgeClimb.Attempts == 0)
                {
                    Svc.Log.Information($"[BOCCHI][跳上低台] 「{AutomationControl.GetName(Kind)}」點 {node} 的寶箱在低台上：到了起跳點 {ledge.Approach:F1}，停下後往 {ledge.Landing:F1} 直線前進並跳躍"
                                        + $"（最多試 {LedgeClimb.MaxAttempts} 次）；角色位置 {player:F2}");
                }

                return false;
            case LedgeAction.RunUp:
                OwnMoveDestinations.Note(ledge.Landing);
                vnav.MoveTo([ledge.Landing], false);
                return false;
            case LedgeAction.Landed:
                StopNavigationForNewTarget();
                Svc.Log.Information($"[BOCCHI][跳上低台] 「{AutomationControl.GetName(Kind)}」點 {node}：第 {ledgeClimb.Attempts + 1} 次站上低台，照常走到寶箱；角色位置 {player:F2}");
                return null;
            case LedgeAction.Retry:
                StopNavigationForNewTarget();
                Svc.Log.Information($"[BOCCHI][跳上低台] 「{AutomationControl.GetName(Kind)}」點 {node} 第 {ledgeClimb.Attempts} 次沒上去（{ledgeClimb.LastFailure}），回起跳點重來；角色位置 {player:F2}");
                return false;
            case LedgeAction.GiveUp:
                StopNavigationForNewTarget();
                var what = $"跳上低台試了 {ledgeClimb.Attempts} 次都沒上去（最後一次：{ledgeClimb.LastFailure}）";
                if (SkipUnreachableStep(what))
                {
                    return false;
                }

                // 沒有已記錄的寶箱可記成走不到：這一輪當作跑過這一點，不停止巡迴。
                Svc.Log.Information($"[BOCCHI][跳上低台] 「{AutomationControl.GetName(Kind)}」點 {node} {what}：這一輪跳過這一點，巡迴繼續。角色位置 {player:F2}");
                return true;
            default:
                return false;
        }
    }

    // 跳躍＝GeneralAction 2（台服 GeneralAction 表）；位址解不出來時不呼叫（同 vnavmesh 跟隨路徑的跳躍）。
    internal static unsafe bool TryJump()
    {
        if (ActionManager.Addresses.Instance.Value == 0 || ActionManager.Addresses.UseAction.Value == 0)
        {
            return false;
        }

        var manager = ActionManager.Instance();
        return manager != null && manager->UseAction(ActionType.GeneralAction, JumpActionId);
    }

    private bool ReturnToBaseCampHandler()
    {
        distance = 0;
        var now = Environment.TickCount64;
        var inCombat = states.GetState() == State.InCombat;
        if (inCombat)
        {
            lastCombatSeenAt = now;
        }

        // 戰鬥中（含旗標剛掉、還沒滿脫戰時間）往營地跑脫戰；連打下一隻時旗標會短暫掉下，單一拍的「不在戰鬥」不算。
        if (!HuntCombatWait.IsSettled(inCombat, lastCombatSeenAt, now))
        {
            RequestMove(Aethernet.BaseCamp.GetData().Position, null);
            return false;
        }

        if (lastCombatSeenAt != 0 && now - lastCombatSeenAt < 10_000)
        {
            Svc.Log.Information($"[BOCCHI][戰鬥等待] 「{AutomationControl.GetName(Kind)}」回營地這一步：脫離戰鬥 {(now - lastCombatSeenAt) / 1000f:F1} 秒，"
                                + $"停下往營地跑的導航（含還在算的尋路），改施放返回。角色位置 {Player.Position:F1}");
        }

        StopNavigationForNewTarget();

        StepProcessor.SubmitFront(ChainHelper.ReturnChain(new ReturnChainConfig
        {
            ApproachAetheryte = true,
        }));

        return true;
    }

    private bool WalkToAethernetHandler()
    {
        var aethernet = CurrentStep.Aethernet;
        var destination = aethernet.GetData().Position;
        if (ClimbShardStairs(aethernet, destination) is { } climbing)
        {
            return climbing;
        }

        if (!RequestMove(destination, TravelSource.HuntShard))
        {
            return false;
        }

        if (!Player.Mounted && !holdingForManual)
        {
            StepProcessor.SubmitFront(ChainHelper.MountChain());
        }

        distance = Player.DistanceTo(destination);
        return distance <= 4f;
    }

    private bool TeleportToAethernetHandler()
    {
        distance = 0;
        StepProcessor.SubmitFront(ChainHelper.TeleportChain(CurrentStep.Aethernet));
        return true;
    }

    private void ResetShardStairs()
    {
        shardStairs = null;
        shardStairsStep = -1;
        shardStairsDoneStep = -1;
    }

    // 晶體在台上、vnavmesh 常卡在台階下（TravelRoutes.ShardStairs）：夠近又在台階下時照 LedgeClimb 走到起跳點跳上去。回 null＝照常走。
    private bool? ClimbShardStairs(Aethernet aethernet, Vector3 shard)
    {
        if (shardStairsDoneStep == stepIndex && shardStairsFor == aethernet)
        {
            return null;
        }

        var player = Player.Position;
        if (shardStairs == null || shardStairsStep != stepIndex || shardStairsFor != aethernet)
        {
            if (!TravelRoutes.TryPickShardStairs(aethernet, shard, player, out var picked))
            {
                return null;
            }

            shardStairs = picked;
            shardStairsStep = stepIndex;
            shardStairsFor = aethernet;
            Svc.Log.Information($"[BOCCHI][階梯跳躍] 「{AutomationControl.GetName(Kind)}」走向乙太之晶「{aethernet.ToFriendlyString()}」，角色在台階下 {player:F1}："
                                + $"先到起跳點 {picked.Approach:F1}、往 {picked.Landing:F1} 跳上台（最多 {LedgeClimb.MaxAttempts} 次）");
        }

        var ledge = shardStairs.Value;
        distance = Vector3.Distance(player, shard);
        var now = Environment.TickCount64;
        var action = ledgeClimb.Step(now, stepIndex, player, ledge.IsOnTop(player, shard), Svc.Condition[ConditionFlag.Jumping], yieldGate.Holding, ledge);
        if (action == LedgeAction.Jump)
        {
            var sent = TryJump();
            Svc.Log.Debug($"[BOCCHI][階梯跳躍] 「{AutomationControl.GetName(Kind)}」第 {ledgeClimb.Attempts + 1} 次：{(sent ? "跳" : "跳躍送不出去")}；角色位置 {player:F2}");
            action = ledgeClimb.JumpSent(sent, now);
        }

        switch (action)
        {
            case LedgeAction.Approach:
                RequestMove(ledge.Approach, TravelSource.HuntShard);
                return false;
            case LedgeAction.Stop:
            case LedgeAction.Retry:
                StopNavigationForNewTarget();
                if (action == LedgeAction.Retry)
                {
                    Svc.Log.Information($"[BOCCHI][階梯跳躍] 「{AutomationControl.GetName(Kind)}」第 {ledgeClimb.Attempts} 次沒上去（{ledgeClimb.LastFailure}），回起跳點重來；角色位置 {player:F2}");
                }

                return false;
            case LedgeAction.RunUp:
                OwnMoveDestinations.Note(ledge.Landing);
                vnav.MoveTo([ledge.Landing], false);
                return false;
            case LedgeAction.Landed:
            case LedgeAction.Proceed:
            case LedgeAction.GiveUp:
                StopNavigationForNewTarget();
                shardStairsDoneStep = stepIndex;
                shardStairs = null;
                Svc.Log.Information(action switch
                {
                    LedgeAction.Landed => $"[BOCCHI][階梯跳躍] 「{AutomationControl.GetName(Kind)}」第 {ledgeClimb.Attempts + 1} 次跳上台了，接著走到晶體；角色位置 {player:F2}",
                    LedgeAction.Proceed => $"[BOCCHI][階梯跳躍] 「{AutomationControl.GetName(Kind)}」還沒跳就已經在台上了，照常走到晶體；角色位置 {player:F2}",
                    _ => $"[BOCCHI][階梯跳躍] 「{AutomationControl.GetName(Kind)}」跳了 {ledgeClimb.Attempts} 次都沒上去（最後一次：{ledgeClimb.LastFailure}），照常讓 vnavmesh 走；角色位置 {player:F2}",
                });
                return null;
            default:
                return false;
        }
    }

    // 一律回 false：做完時自己移除這一步（不前進 stepIndex），原本那一步接著跑。
    private bool DigCarrotHandler()
    {
        var step = CurrentStep;
        switch (OnCarrotStep(step))
        {
            case CarrotStepAction.Walk:
                if (!RequestMove(step.Position, TravelSource.HuntNode))
                {
                    return false;
                }

                distance = Player.DistanceTo(step.Position);
                if (!Player.Mounted && !holdingForManual && distance > CarrotMountDistance)
                {
                    StepProcessor.SubmitFront(ChainHelper.MountChain());
                }

                return false;
            case CarrotStepAction.Hold:
                return false;
            default:
                RemoveCarrotStep();
                return false;
        }
    }
}
