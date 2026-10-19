using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BOCCHI.ActionHelpers;
using BOCCHI.Automation;
using BOCCHI.Data;
using BOCCHI.Modules.Aggro;
using BOCCHI.Modules.Fates;
using BOCCHI.Modules.StateManager;
using BOCCHI.Pathfinding;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameFunctions;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Ocelot.IPC;
using FateContext = FFXIVClientStructs.FFXIV.Client.Game.Fate.FateContext;

namespace BOCCHI.Modules.Automator;

public class FateActivity(EventData data, Lifestream lifestream, VNavmesh vnav, AutomatorModule module, Fate fate)
    : Activity(data, lifestream, vnav, module)
{
    private bool ignoredTargetReported;

    private bool arrivedWaitingReported;

    private const long AggroSnapshotMaxAgeMs = 400;

    // 提早接戰：只存時間戳與旗標；0＝這一場沒有提早接戰。
    private long earlyEngagedAt;

    private bool earlyEngageCombatSeen;

    // 退回過一次舊行為之後，這一場不再提早接戰。
    private bool earlyEngageDisabled;

    private bool earlyEngageAggroReported;

    private AggroModule? aggro;

    private bool aggroResolved;

    // 只在框架執行緒用；建區域時當暫存區，不跨呼叫保存內容。
    private static AggroZone[]? zoneBuffer;

    protected override TaskManagerTask GetPathfindingWatcher(StateManagerModule states)
    {
        var lastTargetPos = Vector3.Zero;
        var lastChaseMs = 0L;
        var stops = new TravelStopGuard();

        return new TaskManagerTask(() =>
        {
            TakeReturnHandOver(stops);
            // 走固定路線時不追敵人：追過去會直接走 vnavmesh 自己算的那條路。跳躍那幾拍只取樣讓位，
            // 不做停下重送（導航本來就是停的，重送會打斷跳躍）。
            var route = TickRoute(TravelSource.IllegalModeFate, stops.Yielding);
            if (route == RouteTick.Hopping)
            {
                ObserveTravel(stops, IsNavigating());
                return false;
            }

            var routing = route == RouteTick.Walking;
            if (EzThrottler.Throttle("FatePathfindingWatcher.EnemyScan", 100))
            {
                var current = Svc.Targets.Target;
                targets.Observe(current?.GameObjectId ?? 0);
                if (current == null)
                {
                    targets.Apply(GetEnemies(), TargetPickMode.Centroid, module.Config.TargetMinHoldSeconds, "趕路補目標");
                }
                else if (ShouldRetargetOnArrival(states) && (current is not IBattleNpc currentNpc || !IsActivityTarget(currentNpc)))
                {
                    var mode = module.Config.ShouldForceTargetCentralEnemy ? TargetPickMode.Centroid : TargetPickMode.Closest;
                    targets.Apply(EngagedFirst(GetEnemies()), mode, module.Config.TargetMinHoldSeconds, "到場換這場的魔物");
                }
            }

            var target = Svc.Targets.Target;
            var chasing = false;
            if (target != null)
            {
                // 只追這場 F.A.T.E. 的敵人。路上打你的怪不追：追過去是往回走，而且一走到就停、整條鏈跟著中止重來。
                chasing = target is IBattleNpc npc && IsActivityTarget(npc);
                if (!chasing)
                {
                    ReportIgnoredTarget();
                }
                else if (!routing && Vector3.Distance(target.Position, lastTargetPos) > 5f && !stops.HoldsOwnMoves(IsNavigating()))
                {
                    OwnMoveDestinations.Note(target.Position);
                    vnav.PathfindAndMoveTo(target.Position, false);
                    lastTargetPos = target.Position;
                    lastChaseMs = Environment.TickCount64;
                }

                if (states.GetState() == State.InFate)
                {
                    var distance = Vector3.Distance(Player.Position, target.Position) - target.HitboxRadius;
                    if (distance <= module.Config.EngagementRange || (chasing && TryEarlyEngage((IBattleNpc)target, distance)))
                    {
                        Actions.TryUnmount();

                        vnav.Stop();

                        return true;
                    }
                }
            }

            var stopped = !IsNavigating();
            ObserveTravel(stops, !stopped);
            if (stopped && chasing && !routing)
            {
                // 追的敵人還沒進交戰距離、移動卻停了：再追一次，最多每秒一次；使用者在手動移動、或導航剛停還沒靜止時不追。
                if (!stops.HoldsOwnMoves(false) && Environment.TickCount64 - lastChaseMs >= 1000)
                {
                    OwnMoveDestinations.Note(target!.Position);
                    vnav.PathfindAndMoveTo(target.Position, false);
                    lastTargetPos = target.Position;
                    lastChaseMs = Environment.TickCount64;
                }

                return false;
            }

            if (PauseForCombat(states, stopped))
            {
                return false;
            }

            if (!stopped)
            {
                KeepTravelling(stops, true, TravelSource.IllegalModeFate);
                return false;
            }

            if (IsInZone())
            {
                if (!arrivedWaitingReported)
                {
                    arrivedWaitingReported = true;
                    Record.MarkArrived(Environment.TickCount64, fate.Progress is { Count: > 0 } samples ? (int)samples.Latest : -1);
                    Svc.Log.Information($"[BOCCHI][非法模式] 已抵達「{GetName()}」，還沒找到這場 F.A.T.E. 可以接戰的敵人：原地等待，不再每秒重新尋路。");
                }

                return false;
            }

            if (KeepTravelling(stops, false, TravelSource.IllegalModeFate))
            {
                return false;
            }

            ReportTravelInterrupt("vnav停下");
            throw new VnavmeshStoppedException();
        }, TravelWatcherConfig(stops));
    }

    // 抵達 F.A.T.E. 範圍內、目前目標不是這場的魔物：換成這場的魔物、優先挑已在戰鬥中的；正在打你、別人剛選的照目標遲滯的規則保留。
    private bool ShouldRetargetOnArrival(StateManagerModule states)
    {
        var config = module.Config;
        return config.ShouldEarlyEngageWhenFateMobEngaged && config.ShouldForceTarget && config.TargetMinHoldSeconds > 0
               && !earlyEngageDisabled && states.GetState() == State.InFate;
    }

    private static List<IBattleNpc> EngagedFirst(List<IBattleNpc> enemies)
    {
        var engaged = enemies.FindAll(IsEngaged);
        return engaged.Count > 0 ? engaged : enemies;
    }

    // 魔物在戰鬥中（跟誰都算）：狀態旗標 InCombat，或名牌是「與其他隊伍交戰中」。
    private static bool IsEngaged(IBattleNpc npc)
    {
        return (npc.StatusFlags & StatusFlags.InCombat) != 0 || npc.GetNameplateKind() == NameplateKind.HostileEngagedOther;
    }

    private bool TryEarlyEngage(IBattleNpc target, float distance)
    {
        var config = module.Config;
        if (!config.ShouldEarlyEngageWhenFateMobEngaged || earlyEngageDisabled)
        {
            return false;
        }

        var role = Svc.Objects.LocalPlayer?.ClassJob.ValueNullable?.Role ?? (byte)0;
        var range = EarlyEngageRules.RangeFor(role, config.EngagementRange, config.RangedEngagementRange);
        if (distance > range)
        {
            return false;
        }

        var verdict = EarlyEngageRules.Evaluate(true, distance, range, IsEngaged(target), false);
        if (verdict == EarlyEngageVerdict.Engage && InAggroZone(target.GameObjectId))
        {
            verdict = EarlyEngageVerdict.InAggroZone;
        }

        if (verdict == EarlyEngageVerdict.InAggroZone && !earlyEngageAggroReported)
        {
            earlyEngageAggroReported = true;
            Svc.Log.Information($"[BOCCHI][非法模式] 「{GetName()}」的魔物已在戰鬥中、距離 {distance:F1} 米，但這裡在別的魔物的感知範圍內：不提早停下，照舊走近到接戰距離 {config.EngagementRange:F0} 米。");
        }

        if (verdict != EarlyEngageVerdict.Engage)
        {
            return false;
        }

        earlyEngagedAt = Environment.TickCount64;
        earlyEngageCombatSeen = false;
        targets.Stats.EarlyEngaged = true;
        Svc.Log.Information($"[BOCCHI][非法模式] 「{GetName()}」的魔物已在戰鬥中、距離 {distance:F1} 米（{EarlyEngageRules.RoleLabel(role)}，接戰距離 {range:F0} 米）：提早下坐騎接戰。");
        return true;
    }

    // 讀不到仇恨快照（顯示與趕路迴避都關著、或快照過期）＝不知道：不擋，與「迴避關著時趕路不避」一致。
    private bool InAggroZone(ulong targetId)
    {
        if (!aggroResolved)
        {
            aggroResolved = true;
            module.TryGetModule(out aggro);
        }

        var player = Svc.Objects.LocalPlayer;
        if (aggro == null || player == null || !aggro.Scanner.Snapshot.IsFresh(Environment.TickCount64, AggroSnapshotMaxAgeMs))
        {
            return false;
        }

        zoneBuffer ??= new AggroZone[AggroZoneSet.Capacity];
        var count = AggroZones.Build(aggro.Scanner.Snapshot.Entries, aggro.BuildZoneSettings(false), zoneBuffer);
        return EarlyEngageRules.InAnyZone(zoneBuffer.AsSpan(0, count), AggroZones.MinState(aggro.Config.AvoidPossible), targetId, player.Position);
    }

    protected override bool FallBackFromEarlyEngage()
    {
        if (earlyEngagedAt == 0)
        {
            return false;
        }

        earlyEngageCombatSeen |= Svc.Condition[ConditionFlag.InCombat];
        if (!EarlyEngageRules.ShouldFallBack(earlyEngagedAt, earlyEngageCombatSeen, Environment.TickCount64))
        {
            return false;
        }

        earlyEngagedAt = 0;
        earlyEngageDisabled = true;
        targets.Stats.EarlyEngageFallBacks++;
        Svc.Log.Information($"[BOCCHI][非法模式] 「{GetName()}」提早接戰後 {EarlyEngageRules.CombatWaitMs / 1000} 秒都沒有進入戰鬥：這一場改回照舊走近到接戰距離 {module.Config.EngagementRange:F0} 米。");
        return true;
    }

    private void ReportIgnoredTarget()
    {
        if (ignoredTargetReported)
        {
            return;
        }

        ignoredTargetReported = true;
        Svc.Log.Information($"[BOCCHI][非法模式] 前往「{GetName()}」途中，目前的目標不是這場 F.A.T.E. 的敵人：不追過去，照原路線前往。");
    }

    protected override float GetRadius()
    {
        // 不走 FatesModule 的 fates[data.Id]（字典索引子）：FATE 一結束 FateTracker 就把它移出 Fates，而這個活動活得比它久，那時索引子會擲 KeyNotFoundException。
        // 建構時傳進來的 fate 與 data 是同一場，半徑相同。
        return fate.Radius;
    }

    // 每幀呼叫：照索引讀 FATE 表的位址比對 id，不建列舉器、閉包與 IFate 包裝；位址當場讀完即丟。
    // 解參條件與 Dalamud FateTable.CreateFateReference 相同：位址非 0、而且 PlayerState 已載入（同一個 IPlayerState 服務）。
    public override unsafe bool IsValid()
    {
        var table = Svc.Fates;
        var count = table.Length;
        if (count == 0 || !Svc.PlayerState.IsLoaded)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            var address = table.GetFateAddress(i);
            if (address != nint.Zero && ((FateContext*)address)->FateId == fate.Id)
            {
                return true;
            }
        }

        return false;
    }

    protected override Vector3 GetPosition()
    {
        return fate.StartPosition;
    }

    protected override TravelRoute? Route
    {
        get => TravelRoutes.Fates.GetValueOrDefault(data.Id);
    }

    public override string GetName()
    {
        return fate.Name;
    }

    /// <summary>這一場的開始時間（Unix 秒；0 以下＝遊戲還沒給）：「換一場」用它分辨同一種 F.A.T.E. 的不同場次。</summary>
    public int StartTimeEpoch
    {
        get => fate.StartTimeEpoch;
    }

    public override int ReadProgress()
    {
        return fate.CurrentProgress;
    }

    public override string KindLabel
    {
        get => "FATE";
    }

    protected override unsafe bool IsActivityTarget(IBattleNpc obj)
    {
        try
        {
            var battleChara = (BattleChara*)obj.Address;

            return battleChara->FateId == data.Id;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex.Message);
            return false;
        }
    }

    protected override ActivityState GetPostPathfindingState()
    {
        return ActivityState.Participating;
    }
}
