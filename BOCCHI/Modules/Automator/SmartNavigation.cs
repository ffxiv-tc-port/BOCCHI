using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BOCCHI.Enums;
using ECommons.DalamudServices;

namespace BOCCHI.Modules.Automator;

public enum NavigationType
{
    Walk,
    ReturnWalk,
    ReturnTeleportWalk,
    WalkTeleportWalk,
}

/// <summary>四種導航方式的成本（距離換算）；順序與 <see cref="NavigationType"/> 相同。</summary>
public readonly record struct NavigationCosts(float Walk, float ReturnWalk, float ReturnTeleportWalk, float WalkTeleportWalk);

public static class SmartNavigation
{
    private const float RETURN_BASE_COST = 75f;

    public static NavigationType Decide(Vector3 playerPosition, Vector3 destination, AethernetData closestToDestination)
    {
        var computed = Costs(playerPosition, destination, closestToDestination);
        var costs = new Dictionary<NavigationType, float>
        {
            { NavigationType.Walk, computed.Walk },
            { NavigationType.ReturnWalk, computed.ReturnWalk },
            { NavigationType.ReturnTeleportWalk, computed.ReturnTeleportWalk },
            { NavigationType.WalkTeleportWalk, computed.WalkTeleportWalk },
        };

        Svc.Log.Debug("Closest Aethernet: " + closestToDestination.Aethernet.ToFriendlyString());
        foreach (var (type, cost) in costs)
        {
            Svc.Log.Debug($"{type} - {cost:f2}");
        }

        return costs.OrderBy(kv => kv.Value).First().Key;
    }

    /// <summary>不寫 log 的成本計算（選活動記錄用）；與 <see cref="Decide"/> 同一套算法。</summary>
    public static NavigationCosts Costs(Vector3 playerPosition, Vector3 destination, AethernetData closestToDestination)
    {
        var closestToPlayer = AethernetData.GetClosestToPlayer();

        var costToWalkToNearestShard = Vector3.Distance(playerPosition, closestToPlayer.Position);
        var costToWalkFromEventShardToEvent = Vector3.Distance(closestToDestination.Position, destination);
        var costToWalkToEventDirectly = Vector3.Distance(playerPosition, destination);

        var costToReturnThenWalk = RETURN_BASE_COST + Vector3.Distance(Aethernet.BaseCamp.GetData().Position, destination);
        var costToReturnTeleportThenWalk = RETURN_BASE_COST + costToWalkFromEventShardToEvent;
        var costToWalkToShardThenEvent = costToWalkToNearestShard + costToWalkFromEventShardToEvent;

        return new NavigationCosts(costToWalkToEventDirectly, costToReturnThenWalk, costToReturnTeleportThenWalk, costToWalkToShardThenEvent);
    }

    /// <summary>最便宜的那一種；同分取列舉順序在前的（與 <see cref="Decide"/> 的穩定排序相同）。</summary>
    public static NavigationType Cheapest(NavigationCosts costs, out float cost)
    {
        var type = NavigationType.Walk;
        cost = costs.Walk;
        Consider(NavigationType.ReturnWalk, costs.ReturnWalk, ref type, ref cost);
        Consider(NavigationType.ReturnTeleportWalk, costs.ReturnTeleportWalk, ref type, ref cost);
        Consider(NavigationType.WalkTeleportWalk, costs.WalkTeleportWalk, ref type, ref cost);
        return type;
    }

    private static void Consider(NavigationType candidate, float value, ref NavigationType type, ref float cost)
    {
        if (value < cost)
        {
            type = candidate;
            cost = value;
        }
    }
}
