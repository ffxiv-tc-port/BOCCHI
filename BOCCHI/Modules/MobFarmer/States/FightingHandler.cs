using System;
using System.Linq;
using BOCCHI.Automation;
using BOCCHI.Pathfinding;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using Ocelot.IPC;
using Ocelot.States;

namespace BOCCHI.Modules.MobFarmer.States;

[State<FarmerPhase>(FarmerPhase.Fighting)]
public class FightingHandler(MobFarmerModule module) : FarmerPhaseHandler(module)
{
    private readonly MoveRequestGuard returnGuard = new();

    public override void Enter()
    {
        base.Enter();
        returnGuard.Reset();
    }

    public override FarmerPhase? Handle()
    {
        var anyInCombat = Module.Scanner.InCombat.Any();
        if (anyInCombat && EzThrottler.Throttle("Targetter"))
        {
            Module.Farmer.Targets.Apply(Module.Scanner.InCombat.ToList(), TargetPickMode.Centroid, Module.Config.TargetMinHoldSeconds, "打怪中");
        }


        var startingPoint = Module.Farmer.StartingPoint;
        var shouldReturnHome = Module.Config.ReturnToStartInWaitingPhase && Player.DistanceTo(startingPoint) >= Module.Config.MinEuclideanDistanceToReturnHome;
        if (shouldReturnHome && !anyInCombat)
        {
            var vnav = Module.GetIPCSubscriber<VNavmesh>();
            switch (returnGuard.Next(startingPoint, Player.Position, vnav.IsRunning(), vnav.IsSimpleMoveInProgress(), vnav.IsNavmeshReady(), Environment.TickCount64))
            {
                case MoveRequestDecision.Send:
                    OwnMoveDestinations.Note(startingPoint);
                    vnav.PathfindAndMoveTo(startingPoint, false);
                    break;
                case MoveRequestDecision.GiveUp:
                    // 跳過走回起點，等下一輪打完再試。
                    Svc.Log.Information($"[BOCCHI][尋路] 自動打怪走回起點 {startingPoint:F1}：{MoveRequestGuard.NoStartLimitMs / 1000} 秒內都沒開始移動"
                                        + $"（vnavmesh 算不出路或一直失敗，這段期間送了 {returnGuard.Sends} 次），這次不走回去。角色位置 {Player.Position:F1}");
                    returnGuard.Reset();
                    return FarmerPhase.Waiting;
            }

            return Player.DistanceTo(startingPoint) <= 2f ? FarmerPhase.Waiting : null;
        }

        if (!anyInCombat && !Svc.Condition[ConditionFlag.InCombat])
        {
            Module.Farmer.RotationPlugin.PhantomJobOff();
            return FarmerPhase.Waiting;
        }

        return null;
    }
}
