using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.Treasure;

namespace BOCCHI.Modules.MapOverlay;

/// <summary>疊加層說明框的文字：樣板由呼叫端照目前語言填好（每次推送前建一份），這裡只做取代。</summary>
public sealed class MapOverlayTexts
{
    /// <summary>依 <see cref="TreasureRoutes.Order"/> 的段名。</summary>
    public required string[] RouteNames { get; init; }

    public string NoRoute { get; init; } = "-";

    /// <summary>這一輪的走法（例：逆向（每輪交替））。</summary>
    public string Direction { get; init; } = string.Empty;

    public required string PlanNodeTemplate { get; init; }

    public required string PlanShardTemplate { get; init; }

    public required string PlanTeleportTemplate { get; init; }

    public required string PlanReturn { get; init; }

    public required string SpawnOpenTemplate { get; init; }

    public required string SpawnVisitedTemplate { get; init; }

    public required string ChestTemplate { get; init; }

    public required string ChestUnmatchedTemplate { get; init; }

    public required string Bronze { get; init; }

    public required string Silver { get; init; }

    public string AvoidedSuffix { get; init; } = string.Empty;

    public string UnreachableSuffix { get; init; } = string.Empty;

    public required string CarrotOpenTemplate { get; init; }

    public required string CarrotDoneTemplate { get; init; }

    public required string CarrotSeenTemplate { get; init; }

    public string RouteName(int route)
    {
        return route >= 0 && route < RouteNames.Length ? RouteNames[route] : NoRoute;
    }

    public string AethernetName(Aethernet aethernet)
    {
        var index = TreasureRoutes.IndexOf(aethernet);
        return index >= 0 ? RouteName(index) : aethernet.ToString();
    }

    public string PlanNode(int route, int station, uint node)
    {
        return PlanNodeTemplate
            .Replace("{route}", RouteName(route))
            .Replace("{direction}", Direction)
            .Replace("{n}", station.ToString())
            .Replace("{node}", node.ToString());
    }

    public string PlanShard(Aethernet aethernet)
    {
        return PlanShardTemplate.Replace("{name}", AethernetName(aethernet));
    }

    public string PlanTeleport(Aethernet aethernet)
    {
        return PlanTeleportTemplate.Replace("{name}", AethernetName(aethernet));
    }

    public string SpawnOpen(int route, uint node)
    {
        return Suffix(SpawnOpenTemplate.Replace("{route}", RouteName(route)).Replace("{node}", node.ToString()), node, false);
    }

    public string SpawnVisited(int route, uint node)
    {
        return Suffix(SpawnVisitedTemplate.Replace("{route}", RouteName(route)).Replace("{node}", node.ToString()), node, false);
    }

    public string Chest(KnownTreasure chest, int route, bool unreachable)
    {
        var type = chest.Type == TreasureType.Silver ? Silver : Bronze;
        if (chest.Node == 0)
        {
            return ChestUnmatchedTemplate.Replace("{type}", type);
        }

        return Suffix(ChestTemplate.Replace("{route}", RouteName(route)).Replace("{node}", chest.Node.ToString()).Replace("{type}", type), chest.Node, unreachable);
    }

    public string CarrotOpen(uint id)
    {
        return CarrotOpenTemplate.Replace("{id}", id.ToString());
    }

    public string CarrotDone(uint id)
    {
        return CarrotDoneTemplate.Replace("{id}", id.ToString());
    }

    public string CarrotSeen(uint id)
    {
        return CarrotSeenTemplate.Replace("{id}", id.ToString());
    }

    private string Suffix(string text, uint node, bool unreachable)
    {
        if (TreasureData.IsAvoided(node))
        {
            text += AvoidedSuffix;
        }

        return unreachable ? text + UnreachableSuffix : text;
    }
}
