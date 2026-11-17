using System;
using System.Collections.Generic;
using System.Linq;
using BOCCHI.Chains;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Ledger;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.Fates;
using BOCCHI.Modules.PhantomJobs;
using BOCCHI.Modules.StateManager;
using BOCCHI.Pathfinding;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Ocelot.Chain;
using Ocelot.IPC;

namespace BOCCHI.Modules.Automator;

public partial class Automator
{
    /// <summary>
    /// 輔助職業練等要換職業時，兩個活動之間先不選下一個（沒有動作鏈在跑，練等那邊的切換條件才會成立）。
    /// 每次暫停最多 <see cref="JobSwitchHoldLimitMs"/>。
    /// </summary>
    private bool HoldForPhantomJobSwitch(AutomatorModule module)
    {
        if (!phantomJobsResolved)
        {
            phantomJobsResolved = true;
            module.TryGetModule(out phantomJobs);
        }

        var leveling = phantomJobs?.Leveling;
        var now = Environment.TickCount64;
        if (leveling is not { IsRunning: true } || !(leveling.WantsSwitch || leveling.IsSwitching) || now < jobSwitchHoldSkipUntil)
        {
            jobSwitchHoldSince = 0;
            return false;
        }

        if (jobSwitchHoldSince == 0)
        {
            jobSwitchHoldSince = now;
            Svc.Log.Information("[BOCCHI][非法模式] 輔助職業練等要切換職業：先不選下一個活動，等切換完成（最多 20 秒）。");
        }

        if (now - jobSwitchHoldSince <= JobSwitchHoldLimitMs)
        {
            return true;
        }

        jobSwitchHoldSince = 0;
        jobSwitchHoldSkipUntil = now + JobSwitchHoldCooldownMs;
        Svc.Log.Information("[BOCCHI][非法模式] 等輔助職業切換超過 20 秒：先繼續非法模式，60 秒內不再為它暫停。");
        return false;
    }

    /// <summary>
    /// 設定「魔法甕優先」：趕路去緊急遭遇戰或別的 F.A.T.E.（還沒進入、沒在戰鬥）時出現有勾選參加的魔法甕，就中止趕路改去魔法甕。
    /// 已經在參加的活動不拋下；返回／傳送的詠唱與讀取中不打斷（同改去緊急遭遇戰）。
    /// </summary>
    private bool TryLeaveTravelForPot(AutomatorModule module, StateManagerModule states, Activity current, Lifestream lifestream, VNavmesh vnav)
    {
        if (!module.Config.ShouldPrioritizePots
            || current is ForkedTowerWaitActivity
            || (current is FateActivity && PotCycle.IsPot(current.data.Id))
            || current.state is not (ActivityState.Idle or ActivityState.Pathfinding)
            || states.GetState() != State.Idle)
        {
            return false;
        }

        var now = Environment.TickCount64;
        if (now < nextPotTravelCheckAt)
        {
            return false;
        }

        nextPotTravelCheckAt = now + LeaveFateTravelCheckIntervalMs;
        if (IsCastingOrBusy() || IsLifestreamBusy(lifestream))
        {
            return false;
        }

        var pot = FindPotFate(module, lifestream, vnav);
        if (pot == null)
        {
            return false;
        }

        var navigation = module.Plugin.Automation.AbortIllegalModeTravel();
        current.LogTargetSummary(ActivityEnd.SwitchedToPot);
        Ledger?.Ended(current, ActivityEnd.SwitchedToPot);
        Activity = pot;
        Ledger?.Selected(pot, ActivitySource.SwitchedToPot);
        var kind = current is CriticalEncounter ? "緊急遭遇戰" : "F.A.T.E.";
        Svc.Log.Information($"[BOCCHI][非法模式] 前往{kind}「{current.GetName()}」途中出現魔法甕「{pot.GetName()}」（設定：魔法甕優先）：中止趕路，改去魔法甕；導航：{navigation}");
        return true;
    }

    /// <summary>
    /// 設定「兩歧塔開放時到傳送魔紋等候」：還能報名、沒被略過時，放下還沒開始參加的活動（待命、趕路、等魔法甕）改去傳送魔紋；
    /// 正在參加的 F.A.T.E.／緊急遭遇戰不拋下（結束後選活動時才去）。戰鬥中、詠唱或傳送中不打斷。回 true＝這一幀換過去了。只在框架執行緒呼叫。
    /// </summary>
    private bool TrySwitchToForkedTower(AutomatorModule module, StateManagerModule states, Lifestream lifestream, VNavmesh vnav)
    {
        if (!module.Config.ShouldWaitAtForkedTowerPad || Activity is ForkedTowerWaitActivity)
        {
            return false;
        }

        var now = Environment.TickCount64;
        if (now < nextForkedTowerCheckAt)
        {
            return false;
        }

        nextForkedTowerCheckAt = now + LeaveFateTravelCheckIntervalMs;
        if (!criticalEncountersResolved)
        {
            criticalEncountersResolved = true;
            module.TryGetModule(out criticalEncounters);
        }

        if (criticalEncounters == null
            || !criticalEncounters.CriticalEncounters.TryGetValue(ForkedTowerPad.BloodEventId, out var tower)
            || !ForkedTowerPad.IsRegistrationOpen(tower.State)
            || IsSkipped(tower)
            || !ForkedTowerPad.TryGetPad(tower.DynamicEventId, out var pad)
            || !EventData.CriticalEncounters.TryGetValue(tower.DynamicEventId, out var data))
        {
            return false;
        }

        if (Svc.Objects.LocalPlayer is not { } player || ForkedTowerPad.IsInsideTower(player))
        {
            return false;
        }

        var current = Activity;
        if (current != null && current is not PotWaitActivity && current.state is not (ActivityState.Idle or ActivityState.Pathfinding))
        {
            if (forkedTowerBusyReportedFor != tower.StartTimestamp)
            {
                forkedTowerBusyReportedFor = tower.StartTimestamp;
                Svc.Log.Information($"[BOCCHI][兩歧塔] 開放報名，但正在參加{current.KindLabel}「{current.GetName()}」（{current.state}）：不拋下，這一場結束後再前往傳送魔紋");
            }

            return false;
        }

        if (states.GetState() != State.Idle || IsCastingOrBusy() || IsLifestreamBusy(lifestream))
        {
            return false;
        }

        var navigation = current != null || IsChainActive ? module.Plugin.Automation.AbortIllegalModeTravel() : "沒有要停的";
        var next = new ForkedTowerWaitActivity(data, lifestream, vnav, module, criticalEncounters, pad);
        if (current is PotWaitActivity wait)
        {
            wait.ReportLeft(next, navigation);
        }
        else if (current != null)
        {
            current.LogTargetSummary(ActivityEnd.SwitchedToForkedTower);
            Ledger?.Ended(current, ActivityEnd.SwitchedToForkedTower);
        }

        Activity = next;
        next.ReportDeparted(current, navigation);
        return true;
    }

    /// <summary>
    /// 設定「魔法甕快出現時先到出生點等」：沒有可接的活動、推估的下一個魔法甕在出發窗內（<see cref="PotWaitRules.WantsWait" />）時，
    /// 改成前往它的出生點附近等。回 true＝這一幀開始等。只在框架執行緒呼叫。
    /// </summary>
    private bool TryStartPotWait(AutomatorModule module, Lifestream lifestream, VNavmesh vnav)
    {
        if (!fatesResolved)
        {
            fatesResolved = true;
            module.TryGetModule(out fates);
        }

        if (fates == null || !PotWaitRules.WantsWait(module.Config, fates.potCycle.Cycle, fates.Config.GetPotCyclePeriod(), DateTime.UtcNow, out var potId, out var remaining))
        {
            return false;
        }

        var wait = new PotWaitActivity(EventData.Fates[potId], lifestream, vnav, module, fates, DateTime.UtcNow + remaining);
        Activity = wait;
        wait.ReportDeparted(PotWaitRules.DepartureMinutes(module.Config));
        return true;
    }

    /// <summary>
    /// 等魔法甕途中（沒在戰鬥、不在 F.A.T.E.／緊急遭遇戰裡）照常選活動：選得到（魔法甕出現、窗外可接的 F.A.T.E.…）就中止等待改去那裡。
    /// 返回／傳送的詠唱與讀取中不打斷。只在框架執行緒呼叫。
    /// </summary>
    private bool TryLeaveWaitForActivity(AutomatorModule module, StateManagerModule states, PotWaitActivity wait, Lifestream lifestream, VNavmesh vnav)
    {
        if (states.GetState() != State.Idle)
        {
            return false;
        }

        var now = Environment.TickCount64;
        if (now < nextLeaveWaitCheckAt)
        {
            return false;
        }

        nextLeaveWaitCheckAt = now + LeaveFateTravelCheckIntervalMs;
        if (IsCastingOrBusy() || IsLifestreamBusy(lifestream))
        {
            return false;
        }

        var next = SelectNext(module, lifestream, vnav, out var source);
        if (next == null)
        {
            return false;
        }

        var navigation = module.Plugin.Automation.AbortIllegalModeTravel();
        wait.ReportLeft(next, navigation);
        Activity = next;
        Ledger?.Selected(next, source);
        return true;
    }

    /// <summary>
    /// 設定「前往 F.A.T.E. 途中改去緊急遭遇戰」：還在趕路（不在 F.A.T.E. 裡、沒在戰鬥）時，有勾選參加的緊急遭遇戰正在報名、
    /// 剩餘報名秒數不超過門檻，就中止 F.A.T.E. 的趕路鏈並換成那一場；之後走非法模式原本前往緊急遭遇戰的路徑。
    /// 返回／傳送的詠唱與讀取中不打斷。魔法甕優先時，前往魔法甕途中、推估魔法甕快出現時都不改（各只寫一次記錄）。
    /// </summary>
    private bool TryLeaveFateTravelForCriticalEncounter(AutomatorModule module, StateManagerModule states, FateActivity fate, Lifestream lifestream, VNavmesh vnav)
    {
        var config = module.Config;
        if (!config.ShouldLeaveFateTravelForCriticalEncounter
            || fate.state is not (ActivityState.Idle or ActivityState.Pathfinding)
            || states.GetState() != State.Idle)
        {
            return false;
        }

        var now = Environment.TickCount64;
        if (now < nextLeaveFateTravelCheckAt)
        {
            return false;
        }

        nextLeaveFateTravelCheckAt = now + LeaveFateTravelCheckIntervalMs;
        if (IsCastingOrBusy() || IsLifestreamBusy(lifestream))
        {
            return false;
        }

        if (!criticalEncountersResolved)
        {
            criticalEncountersResolved = true;
            module.TryGetModule(out criticalEncounters);
        }

        if (criticalEncounters == null)
        {
            return false;
        }

        var threshold = config.LeaveFateTravelForCriticalEncounterSeconds;
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var keepPot = config.ShouldPrioritizePots && PotCycle.IsPot(fate.data.Id);
        var potDue = TryGetPotDueSoon(module, out var potId, out var potRemaining) && !keepPot;
        foreach (var encounter in criticalEncounters.CriticalEncounters.Values)
        {
            if (encounter.State != DynamicEventState.Register
                || !config.TryGetCriticalEncounterEnabled(encounter.DynamicEventId, out var on) || !on
                || IsSkipped(encounter)
                || !EventData.CriticalEncounters.TryGetValue(encounter.DynamicEventId, out var data)
                || !CriticalEncounterTravelRules.ShouldLeaveFateTravel(threshold, encounter.StartTimestamp, nowUnix, out var remaining))
            {
                continue;
            }

            var left = remaining >= 0 ? $"報名剩 {remaining} 秒" : "讀不到剩餘報名時間";
            if (keepPot)
            {
                if (!ReferenceEquals(potKeptReportedFor, fate))
                {
                    potKeptReportedFor = fate;
                    Svc.Log.Information($"[BOCCHI][非法模式] 前往魔法甕「{fate.GetName()}」途中，緊急遭遇戰「{encounter.Name}」{left}：設定魔法甕優先，不改去緊急遭遇戰");
                }

                return false;
            }

            if (potDue)
            {
                ReportPotSkip(module, encounter, fate, potId, potRemaining);
                return false;
            }

            var navigation = module.Plugin.Automation.AbortIllegalModeTravel();
            fate.LogTargetSummary(ActivityEnd.SwitchedToCriticalEncounter);
            Ledger?.Ended(fate, ActivityEnd.SwitchedToCriticalEncounter);
            Activity = new CriticalEncounter(data, lifestream, vnav, module, criticalEncounters);
            Ledger?.Selected(Activity, ActivitySource.SwitchedToCriticalEncounter);
            Svc.Log.Information($"[BOCCHI][非法模式] 前往 F.A.T.E.「{fate.GetName()}」途中，緊急遭遇戰「{encounter.Name}」{left}（門檻 {threshold} 秒）：中止 F.A.T.E. 的趕路，改去緊急遭遇戰；導航：{navigation}");
            return true;
        }

        return false;
    }

    // 參加過的魔法甕結束後一兩秒財寶提示才到，EurekaHelper 收到才開始尋寶：先等它一下再選下一個活動。沒參加到不會有提示。
    private void ArmPotHuntGrace(Activity ended)
    {
        if (ended is not FateActivity || !PotCycle.IsPot(ended.data.Id) || ended.state is not (ActivityState.Participating or ActivityState.Done)
            || !potHuntIpc.TryRead(out _))
        {
            return;
        }

        potHunt.ArmGrace(Environment.TickCount64);
        nextPotHuntPollAt = 0;
        Svc.Log.Information($"[BOCCHI][魔法甕尋寶] 魔法甕「{ended.GetName()}」結束：先等 EurekaHelper 開始尋寶（最多 {PotHuntYield.StartGraceMs / 1000} 秒），這段時間不選下一個活動。");
    }

    /// <summary>
    /// EurekaHelper 在找魔法甕的財寶（或魔法甕剛結束、還在等它開始）時回 true：先不選下一個活動、不出發。
    /// 正在趕路（或待命返回）就中止自己的動作鏈，vnavmesh 只停 BOCCHI 自己的路線；結束後活動從目前位置接續。只在框架執行緒呼叫。
    /// </summary>
    private bool HoldForPotHunt(AutomatorModule module, StateManagerModule states)
    {
        var now = Environment.TickCount64;
        if (potHuntHolding && module.Config.YieldToManualMovement)
        {
            // 每拍讀按鍵：等完（含上限）接著出發時，返回前問得到你最近有沒有在操作（HandsOnRecent）。
            potHandsOn ??= new HandsOnProbe();
            potHandsOn.ReadInput(now);
        }

        if (now >= nextPotHuntPollAt)
        {
            nextPotHuntPollAt = now + PotHuntPollMs;
            var available = potHuntIpc.TryRead(out var active);
            potHuntHolding = potHunt.Step(now, available, active, out var ev, out var heldMs);
            ReportPotHunt(ev, heldMs);
            if (ev == PotHuntEvent.HuntEnded)
            {
                potChest.Arm(now);
                Svc.Log.Information($"[BOCCHI][財寶箱讓位] 魔法甕尋寶結束：先不選下一個活動、不出發，等你開挖出來的財寶箱（{PotChestSettle.NearbyM:F0} 米內還有財寶箱、"
                                    + $"或你在開箱／對話中就繼續等；連續 {PotChestSettle.QuietMs / 1000} 秒都沒有才繼續，看過財寶箱之後改等 {PotChestSettle.QuietAfterChestMs / 1000} 秒"
                                    + $"（等「第二處財寶」），最多等 {PotChestSettle.CapMs / 1000} 秒）。");
            }

            if (potHuntHolding)
            {
                potChest.Reset();
            }
            else if (potChest.IsArmed)
            {
                potHuntHolding = potChest.Step(now, PotChestBusy(), SecondTreasureNotice.Seen, SecondTreasureNotice.SeenAt, out var chestEvent, out var chestHeldMs);
                ReportPotChest(chestEvent, chestHeldMs);
            }

            if (!potHuntHolding)
            {
                potHuntTravelYielded = false;
            }
        }

        if (!potHuntHolding)
        {
            return false;
        }

        if (potHunt.IsHunting && !potHuntTravelYielded && IsChainActive && (Activity != null || IsIdleReturnRunning())
            && (states.GetState() is State.Idle or State.InCombat) && !Svc.Condition[ConditionFlag.BetweenAreas] && !Svc.Condition[ConditionFlag.BetweenAreas51])
        {
            potHuntTravelYielded = true;
            var navigation = module.Plugin.Automation.YieldIllegalModeTravel();
            var what = Activity == null ? "待命返回" : $"前往{Activity.KindLabel}「{Activity.GetName()}」";
            Svc.Log.Information($"[BOCCHI][魔法甕尋寶] {what}途中 EurekaHelper 在尋寶：中止自己的動作鏈，等它找完再接續；導航：{navigation}");
        }

        return true;
    }

    // 在開箱、對話一類的互動事件裡，或附近有可選取的魔法甕財寶箱；只在框架執行緒、只在這一幀讀物件表，不保存物件。
    internal static bool PotChestBusy()
    {
        var condition = Svc.Condition;
        if (condition[ConditionFlag.OccupiedInEvent] || condition[ConditionFlag.OccupiedInQuestEvent])
        {
            return true;
        }

        var player = Svc.Objects.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        var from = player.Position;
        foreach (var obj in Svc.Objects)
        {
            if (obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj && obj.IsTargetable && PotChestSettle.IsPotChest(obj.BaseId)
                && System.Numerics.Vector3.Distance(from, obj.Position) <= PotChestSettle.NearbyM)
            {
                return true;
            }
        }

        return false;
    }

    private void ReportPotChest(PotChestSettleEvent ev, long heldMs)
    {
        var seconds = LedgerRules.Seconds(heldMs);
        switch (ev)
        {
            case PotChestSettleEvent.Quiet:
                Svc.Log.Information($"[BOCCHI][財寶箱讓位] 附近沒有魔法甕財寶箱、也沒在開箱已 {PotChestSettle.QuietMs / 1000} 秒（共等了 {seconds} 秒，"
                                    + $"{(potChest.SawChest ? "期間看過財寶箱或開箱" : "期間沒看到財寶箱")}）：繼續非法模式。");
                break;
            case PotChestSettleEvent.CapReached:
                Svc.Log.Information($"[BOCCHI][財寶箱讓位] 等開財寶箱滿 {PotChestSettle.CapMs / 1000} 秒（附近還有財寶箱或還在開箱）：先繼續非法模式。");
                break;
            case PotChestSettleEvent.SecondTreasure:
                Svc.Log.Information($"[BOCCHI][財寶箱讓位] 收到「第二處財寶」（尋寶結束後 {seconds} 秒）：繼續不選活動、不出發，等 EurekaHelper 開始找第二處（最多 {PotChestSettle.SecondTreasureWaitMs / 1000} 秒）。");
                break;
            case PotChestSettleEvent.SecondTreasureExpired:
                Svc.Log.Information($"[BOCCHI][財寶箱讓位] 收到「第二處財寶」後 {PotChestSettle.SecondTreasureWaitMs / 1000} 秒 EurekaHelper 都沒開始找（共等了 {seconds} 秒）：繼續非法模式。");
                break;
        }
    }

    private static bool IsIdleReturnRunning()
    {
        return ChainManager.Queues.TryGetValue(Plugin.MainChainQueue, out var main) && main.CurrentChain?.Name == nameof(ReturnChain);
    }

    private void ReportPotHunt(PotHuntEvent ev, long heldMs)
    {
        var seconds = LedgerRules.Seconds(heldMs);
        switch (ev)
        {
            case PotHuntEvent.HuntStarted:
                var next = Activity == null ? "目前沒有活動" : $"之後前往{Activity.KindLabel}「{Activity.GetName()}」";
                Svc.Log.Information($"[BOCCHI][魔法甕尋寶] EurekaHelper 開始找魔法甕的財寶（會自己移動）：先不選下一個活動、不出發，等它找完（最多 {PotHuntYield.HuntCapMs / 1000} 秒）；{next}。");
                break;
            case PotHuntEvent.HuntEnded:
                Svc.Log.Information($"[BOCCHI][魔法甕尋寶] EurekaHelper 尋寶結束（等了 {seconds} 秒）：繼續非法模式。");
                break;
            case PotHuntEvent.GraceExpired:
                Svc.Log.Information($"[BOCCHI][魔法甕尋寶] 魔法甕結束後 {seconds} 秒內 EurekaHelper 沒有開始尋寶：照常選下一個活動。");
                break;
            case PotHuntEvent.CapReached:
                Svc.Log.Information($"[BOCCHI][魔法甕尋寶] 等 EurekaHelper 尋寶超過 {PotHuntYield.HuntCapMs / 1000} 秒：先繼續非法模式，它這一輪結束之前不再等。");
                break;
            case PotHuntEvent.Lost:
                Svc.Log.Information($"[BOCCHI][魔法甕尋寶] 等尋寶途中問不到 EurekaHelper（等了 {seconds} 秒）：不再等，繼續非法模式。");
                break;
        }
    }

    private static void ReportCombatHold(long heldMs)
    {
        var line = $"[BOCCHI][等脫離戰鬥] 秒={LedgerRules.Seconds(heldMs)}（沒有進行中的活動時，戰鬥中不選下一個活動；脫離戰鬥後才選）";
        if (heldMs >= CombatHoldReportMs)
        {
            Svc.Log.Information(line);
        }
        else
        {
            Svc.Log.Debug(line);
        }
    }
}
