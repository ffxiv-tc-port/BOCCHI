using System;
using System.Numerics;
using BOCCHI.Automation;
using BOCCHI.Pathfinding;
using ECommons.Automation.NeoTaskManager;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;
using Ocelot.IPC;

namespace BOCCHI.Chains;

public class PathfindAndMoveToChain : ChainFactory
{
    private readonly Vector3 destination;

    private readonly TravelSource source;

    private readonly VNavmesh vnav;

    public PathfindAndMoveToChain(VNavmesh vnav, Vector3 destination, TravelSource source, float maxRadius = 1f, float minRadius = 0f)
    {
        this.vnav = vnav;
        this.destination = destination;
        this.source = source;
    }

    public static PathfindAndMoveToChain RandomNearby(
        VNavmesh vnav,
        Vector3 destination,
        TravelSource source,
        float maxRadius = 1f,
        float minRadius = 0f)
    {
        var angle = (float)(Random.Shared.NextDouble() * MathF.Tau);
        var distance = minRadius + (float)(Random.Shared.NextDouble() * (maxRadius - minRadius));

        var offsetX = MathF.Cos(angle) * distance;
        var offsetZ = MathF.Sin(angle) * distance;

        destination = new Vector3(destination.X + offsetX, destination.Y, destination.Z + offsetZ);
        destination = DestinationFloor.Project(vnav, destination);

        return new PathfindAndMoveToChain(vnav, destination, source);
    }


    protected override Chain Create(Chain chain)
    {
        // 登記在送出尋路的前一步：趕路迴避只套用到有登記的移動。
        return chain
            .Then(_ => TravelIntent.Register(destination, source))
            .PathfindAndMoveTo(vnav, destination);
    }

    public override TaskManagerConfiguration? Config()
    {
        return new TaskManagerConfiguration
        {
            TimeLimitMS = 180000,
        };
    }
}
