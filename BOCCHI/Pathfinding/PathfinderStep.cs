using System.Numerics;
using BOCCHI.Enums;

namespace BOCCHI.Pathfinding;

public class PathfinderStep
{
    public PathfinderStepType Type;

    public uint NodeId = 0;

    public Aethernet Aethernet = Aethernet.BaseCamp;

    /// <summary>DigCarrot：胡蘿蔔物件的 GameObjectId（每拍用它重查物件表）與插隊當下抄的位置。</summary>
    public ulong ObjectId;

    public Vector3 Position;

    public static PathfinderStep DigCarrot(ulong objectId, Vector3 position)
    {
        return new PathfinderStep
        {
            Type = PathfinderStepType.DigCarrot,
            ObjectId = objectId,
            Position = position,
        };
    }

    public static PathfinderStep WalkToDestination(uint id)
    {
        return new PathfinderStep
        {
            Type = PathfinderStepType.WalkToNode,
            NodeId = id,
        };
    }

    public static PathfinderStep WalkToAethernet(Aethernet aethernet)
    {
        return new PathfinderStep
        {
            Type = PathfinderStepType.WalkToAethernet,
            Aethernet = aethernet,
        };
    }

    public static PathfinderStep TeleportToAethernet(Aethernet aethernet)
    {
        return new PathfinderStep
        {
            Type = PathfinderStepType.TeleportToAethernet,
            Aethernet = aethernet,
        };
    }

    public static PathfinderStep ReturnToBaseCamp()
    {
        return new PathfinderStep
        {
            Type = PathfinderStepType.ReturnToBaseCamp,
        };
    }
}
