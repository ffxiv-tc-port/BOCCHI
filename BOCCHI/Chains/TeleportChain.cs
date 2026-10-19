using System.Linq;
using BOCCHI.Automation;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.Teleporter;
using Dalamud.Game.ClientState.Conditions;
using ECommons.Automation.NeoTaskManager;
using ECommons.GameHelpers;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;
using Ocelot.IPC;

namespace BOCCHI.Chains;

public class TeleportChain(Aethernet aethernet, Lifestream lifestream, TeleporterModule module) : ChainFactory
{
    protected override Chain Create(Chain chain)
    {
        var vnav = module.GetIPCSubscriber<VNavmesh>();
        var nearby = ZoneData.GetNearbyAethernetShards(AethernetData.DISTANCE);
        if (nearby.Count <= 0)
        {
            return CreateRetry(chain, vnav);
        }

        chain.Then(_ => lifestream.Abort());
        chain.BreakIf(() => nearby.Count <= 0);

        var nearest = nearby.First();
        if (Player.DistanceTo(nearest.Position) >= AethernetData.DISTANCE)
        {
            chain.Then(new PathfindAndMoveToChain(vnav, nearest.Position, TravelSource.NearestShard));
            chain.Then(_ => lifestream.GetActiveCustomAetheryte() != 0 && Player.DistanceTo(nearest.Position) < AethernetData.DISTANCE);
        }

        chain.Then(_ => vnav.Stop());
        chain.Then(_ => lifestream.AethernetTeleportByPlaceNameId((uint)aethernet));
        chain.WaitToCycleCondition(ConditionFlag.BetweenAreas);
        // Mount if we should mount and not pathfind, otherwise let the pathfinder handle it
        chain.ConditionalThen(_ => module.Config is { ShouldMount: true, PathToDestination: false }, ChainHelper.MountChain());

        return chain;
    }

    // 建鏈當下 AethernetData.DISTANCE 內沒有乙太之晶：寫明原因，近的話等角色停下後重新靠近再試（有次數與時間上限），
    // 還是不行就不傳送、照原流程往下（步行）。量的是物件表裡乙太之晶物件的位置、3D 距離。
    private Chain CreateRetry(Chain chain, VNavmesh vnav)
    {
        var retry = new ShardApproach(aethernet);
        if (!retry.Begin())
        {
            return chain;
        }

        for (var i = 0; i < ShardRetryRules.MaxApproaches; i++)
        {
            chain.ConditionalThen(_ => retry.ShouldApproach(), () => Chain.Create("TeleportChain.Approach")
                    .Then(new TaskManagerTask(() => retry.WaitUntilStill(), "TeleportChain.WaitUntilStill",
                        new TaskManagerConfiguration { TimeLimitMS = (int)ShardRetryRules.StillWaitLimitMs + 5000 }))
                    .ConditionalThen(_ => retry.MayMove, new ApproachAethernetChain(vnav, retry.Center))
                    .Then(new TaskManagerTask(() => retry.WaitArrived(vnav), "TeleportChain.WaitArrived",
                        new TaskManagerConfiguration { TimeLimitMS = (int)ShardRetryRules.ArriveLimitMs + 5000 })),
                new TaskManagerConfiguration { TimeLimitMS = 240_000 });
        }

        chain.BreakIf(() => !retry.Finish());
        chain.Then(_ => lifestream.Abort());
        chain.Then(_ => vnav.Stop());
        chain.Then(_ => lifestream.AethernetTeleportByPlaceNameId((uint)aethernet));
        chain.WaitToCycleCondition(ConditionFlag.BetweenAreas);
        chain.ConditionalThen(_ => module.Config is { ShouldMount: true, PathToDestination: false }, ChainHelper.MountChain());

        return chain;
    }
}
