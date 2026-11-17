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
    private static bool IsTravelStep(PathfinderStepType type)
    {
        return type is PathfinderStepType.WalkToNode or PathfinderStepType.WalkToAethernet or PathfinderStepType.ReturnToBaseCamp or PathfinderStepType.DigCarrot;
    }

    /// <summary>上下坐騎、詠唱、跳躍、讀圖中：導航停著是暫時的，不判斷手動移動（同非法模式趕路監看）。</summary>
    private static bool IsMovementPaused()
    {
        var condition = Svc.Condition;
        return condition[ConditionFlag.Mounting] || condition[ConditionFlag.Mounting71] || condition[ConditionFlag.MountOrOrnamentTransition]
               || condition[ConditionFlag.Casting] || condition[ConditionFlag.Casting87]
               || condition[ConditionFlag.Jumping] || condition[ConditionFlag.Jumping61]
               || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51];
    }

    // 每幀（步驟處理器沒在跑時）呼叫；只讀戰鬥與騎乘旗標。等待中暫停「同一步太久」的計時（同讓位）。
    private void ObserveCombat(long now)
    {
        var inCombat = Svc.Condition[ConditionFlag.InCombat];
        if (inCombat)
        {
            lastCombatSeenAt = now;
        }

        var step = combatWait.Observe(inCombat, Svc.Condition[ConditionFlag.Mounted], now);
        watchedStepSince += step.HeldMs;

        if (step.Event == HuntCombatEvent.None)
        {
            return;
        }

        var name = AutomationControl.GetName(Kind);
        var at = $"第 {stepIndex + 1} 步（{CurrentStep.Type}）";
        switch (step.Event)
        {
            case HuntCombatEvent.Started:
                var stopped = StopNavigationForCombat(now);
                ResetYield();
                ledgeClimb.Reset();
                ResetShardStairs();
                Svc.Log.Information($"[BOCCHI][戰鬥等待] 「{name}」{at}進入戰鬥：{stopped}原地等戰鬥結束（讓戰鬥 AI 或你自己打完；這段期間不送導航、不叫坐騎、不判斷手動移動），"
                                    + $"脫離戰鬥 {HuntCombatWait.SettleMs / 1000f:F1} 秒後從目前位置接續；最多等 {HuntCombatWait.MaxWaitMs / 1000} 秒。角色位置 {Player.Position:F1}{(Player.Mounted ? "（騎乘中）" : string.Empty)}");
                break;
            case HuntCombatEvent.Ended:
                ResetYield();
                moveGuard.Reset();
                Svc.Log.Information($"[BOCCHI][戰鬥等待] 「{name}」脫離戰鬥 {HuntCombatWait.SettleMs / 1000f:F1} 秒：這次等了 {step.WaitedMs / 1000f:F1} 秒，從目前位置 {Player.Position:F1} 接續{at}");
                break;
            case HuntCombatEvent.Expired:
                ResetYield();
                moveGuard.Reset();
                Svc.Log.Information($"[BOCCHI][戰鬥等待] 「{name}」{at}等了 {HuntCombatWait.MaxWaitMs / 1000} 秒還在戰鬥：這場戰鬥退回原本的做法（照常送導航"
                                    + $"{(CurrentStep.Type == PathfinderStepType.ReturnToBaseCamp ? "，返回這一步照舊往營地跑" : string.Empty)}；手動移動照常讓位），脫離戰鬥後下一場再原地等。角色位置 {Player.Position:F1}");
                break;
            case HuntCombatEvent.ExpiredOver:
                Svc.Log.Information($"[BOCCHI][戰鬥等待] 「{name}」退回原本做法的那場戰鬥結束了（從開始等算起 {step.WaitedMs / 1000f:F1} 秒），之後再進入戰鬥照樣原地等");
                break;
            case HuntCombatEvent.Riding:
                Svc.Log.Information($"[BOCCHI][戰鬥等待] 「{name}」{at}騎乘中進入戰鬥：繼續走，不原地等（騎著不會出招，騎走會脫離戰鬥）；還在戰鬥時下了坐騎才原地等。角色位置 {Player.Position:F1}");
                break;
        }
    }

    // 回傳給記錄用的前綴。Lifestream 在執行任務（你在地圖上點了目的地）時不停：那是你自己要的移動。
    private string StopNavigationForCombat(long now)
    {
        if (yieldGate.Manual.External || lifestreamBusy?.Query(now) == true)
        {
            return "Lifestream 正在執行任務，不停它；";
        }

        if (!vnav.IsRunning() && !vnav.IsSimpleMoveInProgress())
        {
            moveGuard.Reset();
            return string.Empty;
        }

        StopNavigationForNewTarget();
        return "停下 BOCCHI 自己的導航，";
    }

    // 每幀（步驟處理器沒在跑時）呼叫；取樣約每 100 毫秒一次，選項關著時什麼都不讀。讓位中暫停「同一步太久」的計時。
    // 戰鬥中原地等的時候不判斷：那時角色的移動是戰鬥 AI 在打，不是使用者在操作。
    private void ObserveManualMovement(long now)
    {
        yieldGate.Enabled = config.YieldToManualMovement;
        if (!yieldGate.Enabled || stepIndex >= Steps.Count || !IsTravelStep(CurrentStep.Type) || combatWait.Holding)
        {
            if (yieldStep != -1)
            {
                ResetYield();
            }

            return;
        }

        if (yieldStep != stepIndex)
        {
            if (yieldGate.Manual.Expired)
            {
                yieldGate.Reset();
            }

            yieldGate.OnStepChanged();
            yieldStep = stepIndex;
        }

        // 按鍵每拍都讀：短按可能落在兩次取樣之間。不經建構子建出的物件第一次用時才建。
        handsOn ??= new HandsOnProbe();
        var input = handsOn.ReadInput(now);
        if (yieldGate.Manual.External && OwnPathStop.Stops(StopOwnPath("為 Lifestream 讓位中", lifestreamNotice)))
        {
            Svc.Log.Debug($"[BOCCHI][巡迴讓位] 「{AutomationControl.GetName(Kind)}」為 Lifestream 讓位中 vnavmesh 開始走（BOCCHI 先前送的路算完了）：停下");
        }

        var player = Svc.Objects.LocalPlayer;
        if (!yieldGate.ShouldSample(now) || player == null)
        {
            return;
        }

        var navigating = vnav.IsRunning() || vnav.IsSimpleMoveInProgress();
        var interacting = !input && HandsOnProbe.Interacting(now);
        var step = yieldGate.Observe(player.Position, navigating, !navigating && IsMovementPaused(), lifestreamBusy?.Query(now) == true, input || interacting, now);
        if (step.YieldedMs > 0)
        {
            segmentYieldMs += step.YieldedMs;
            watchedStepSince += step.YieldedMs;
        }

        if (yieldGate.Manual.Yielding)
        {
            HandsOnRecent.Note(now, "巡迴正在讓位給你操作");
        }

        switch (step.Event)
        {
            case TravelYieldEvent.Touched when yieldGate.Manual.Capped:
                ReportYield(ref cappedTouchReported, $"[BOCCHI][巡迴讓位] 「{AutomationControl.GetName(Kind)}」第 {stepIndex + 1} 步讓位滿 {ManualMoveDetector.MaxYieldMs / 1000} 秒後偵測到你還在操作"
                                                     + $"（{(input ? "按了移動鍵" : "在互動：開寶箱、對話一類")}）：不重送，改成按鍵／互動讓位；沒有按鍵、沒在互動、角色靜止 {ManualMoveDetector.ResumeQuietMs / 1000f:F1} 秒後從目前位置接續；"
                                                     + $"這種讓位每一步累計上限 {ManualMoveDetector.TouchMaxMs / 1000} 秒。角色位置 {player.Position:F1}");
                break;
            case TravelYieldEvent.Touched:
                ReportYield(ref touchReported, $"[BOCCHI][巡迴讓位] 「{AutomationControl.GetName(Kind)}」第 {stepIndex + 1} 步（{CurrentStep.Type}）導航停下時偵測到你在操作"
                                               + $"（{(input ? "按了移動鍵" : "在互動：開寶箱、對話一類")}）：不重送；沒有按鍵、沒在互動、角色靜止 {ManualMoveDetector.ResumeQuietMs / 1000f:F1} 秒後從目前位置接續；"
                                               + $"這種讓位每一步累計上限 {ManualMoveDetector.TouchMaxMs / 1000} 秒。角色位置 {player.Position:F1}");
                break;
            case TravelYieldEvent.TouchLimit:
                Svc.Log.Information($"[BOCCHI][巡迴讓位] 「{AutomationControl.GetName(Kind)}」第 {stepIndex + 1} 步為按鍵／互動讓位累計超過 {ManualMoveDetector.TouchMaxMs / 1000} 秒："
                                    + $"這一步之後只看持續移動，停下就照常重送。角色位置 {player.Position:F1}");
                break;
            case TravelYieldEvent.External:
                var stopped = navigating && StopNavigationForLifestream();
                ReportYield(ref externalReported, $"[BOCCHI][巡迴讓位] 「{AutomationControl.GetName(Kind)}」第 {stepIndex + 1} 步：Lifestream 正在執行任務（多半是你在地圖上點了目的地）："
                                                  + $"{(stopped ? "停下 BOCCHI 自己的導航、" : string.Empty)}讓位到它做完，這段時間不算進 {ManualMoveDetector.MaxYieldMs / 1000} 秒上限；"
                                                  + $"它做完、角色靜止 {ManualMoveDetector.ResumeQuietMs / 1000f:F1} 秒後從目前位置接續。角色位置 {player.Position:F1}");
                break;
            case TravelYieldEvent.ExternalLimit:
                Svc.Log.Information($"[BOCCHI][巡迴讓位] 「{AutomationControl.GetName(Kind)}」第 {stepIndex + 1} 步：為 Lifestream 讓位累計超過 {ManualMoveDetector.ExternalMaxMs / 1000} 秒，"
                                    + $"之後不再為它讓位（照手動移動的規則）。角色位置 {player.Position:F1}");
                break;
            case TravelYieldEvent.Started:
                ReportYield(ref yieldReported, $"[BOCCHI][巡迴讓位] 「{AutomationControl.GetName(Kind)}」第 {stepIndex + 1} 步（{CurrentStep.Type}）偵測到手動移動"
                                               + $"（導航停著、角色以 {step.Speed:F1} 米/秒持續移動 {ManualMoveDetector.ConfirmMs / 1000f:F1} 秒）：暫停自動前往，"
                                               + $"角色靜止 {ManualMoveDetector.ResumeQuietMs / 1000f:F1} 秒後從目前位置接續；超過 {ManualMoveDetector.MaxYieldMs / 1000} 秒這一步改回照常重送。角色位置 {player.Position:F1}");
                break;
            case TravelYieldEvent.Expired:
                Svc.Log.Information($"[BOCCHI][巡迴讓位] 「{AutomationControl.GetName(Kind)}」第 {stepIndex + 1} 步手動移動超過 {ManualMoveDetector.MaxYieldMs / 1000} 秒：這一步改回照常重送。角色位置 {player.Position:F1}");
                break;
            case TravelYieldEvent.Cancelled:
                Svc.Log.Debug($"[BOCCHI][巡迴讓位] 「{AutomationControl.GetName(Kind)}」讓位中導航又開始走：結束讓位、不另外接續");
                break;
        }
    }

    // 同一段第一次寫 Information，之後寫 Debug。
    private static void ReportYield(ref bool reported, string line)
    {
        if (reported)
        {
            Svc.Log.Debug(line);
            return;
        }

        reported = true;
        Svc.Log.Information(line);
    }
}
