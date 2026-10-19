namespace BOCCHI.Pathfinding;

public enum PathfinderStepType
{
    WalkToNode,
    ReturnToBaseCamp,
    WalkToAethernet,
    TeleportToAethernet,

    /// <summary>寶箱巡迴途中插隊去挖幸運胡蘿蔔；做完就從清單移除，不算進規劃的點。</summary>
    DigCarrot,
}
