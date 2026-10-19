using System;
using System.Numerics;
using BOCCHI.Automation;
using BOCCHI.Data;
using Ocelot.Chain;
using Ocelot.IPC;

namespace BOCCHI.Chains;

public class PathfindingChain : ChainFactory
{
    private readonly EventData data;

    private readonly Vector3 destination;

    private readonly float? maxRadius;

    private readonly float? minRadius;

    private readonly TravelSource source;

    private readonly VNavmesh vnav;

    public PathfindingChain(
        VNavmesh vnav,
        Vector3 destination,
        EventData data,
        TravelSource source,
        float? maxRadius = null,
        float? minRadius = null)
    {
        this.vnav = vnav;
        this.destination = destination;
        this.data = data;
        this.source = source;
        this.maxRadius = maxRadius;
        this.minRadius = minRadius;
    }

    // 基底的 Factory() 會先建一條以型別命名的鏈再交給 Create；這裡改建原本的 "Pathfinding"。
    // Create 只往傳入的鏈接步驟、不另建鏈：另建會讓傳入那條變成沒人等、沒人回收的孤兒鏈。
    public override Func<Chain> Factory()
    {
        return () => Create(Chain.Create("Pathfinding"));
    }

    protected override Chain Create(Chain chain)
    {
        return chain
            .Then(PathfindAndMoveToChain.RandomNearby(vnav, destination, source, maxRadius ?? 1f, minRadius ?? 0f));
    }
}
