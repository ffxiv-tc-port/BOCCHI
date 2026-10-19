using System.Collections.Generic;
using System.Numerics;
using BOCCHI.Data;
using BOCCHI.Pathfinding;

namespace BOCCHI.Modules.Treasure;

public enum TreasureDetourKind
{
    None,

    /// <summary>插在目前這一步前面，先去開。</summary>
    Now,

    /// <summary>不在剩下的路線上、也不在附近：排到路線最後。</summary>
    Append,
}

public readonly record struct TreasureDetour(TreasureDetourKind Kind, KnownTreasure Chest, float Distance);

/// <summary>
/// 寶箱巡迴進行中，已記錄的寶箱什麼時候先去開。純邏輯、不碰遊戲、不配置；只在框架執行緒用。
/// 插隊（Now）：目前在走路（往生成點或乙太之晶），寶箱在偵測範圍內，而且比目前要去的點近、或根本不在剩下的路線上；
/// 往乙太之晶走（接著要傳送離開）時範圍內的一律先開。比目前目標遠、又在路線上的，等走到目前目標再判一次（順路）。
/// 排最後（Append）：不在剩下的路線上、也不在範圍內（例：傳送或返回途中看到、走遠了）。
/// 只考慮對得上生成點、在這一輪區域等級範圍內、這一輪還沒試過、沒有前往失敗過、不在刻意不去的生成點的寶箱。
/// </summary>
public static class TreasureDetourRules
{
    public static TreasureDetour Pick(List<PathfinderStep> steps, int index, Vector3 player, float range, TreasureHuntMemory memory, HashSet<uint> allowed, HashSet<uint> attempted)
    {
        if (index < 0 || index >= steps.Count)
        {
            return default;
        }

        var current = steps[index];
        var walking = current.Type is PathfinderStepType.WalkToNode or PathfinderStepType.WalkToAethernet;
        var currentNode = current.Type == PathfinderStepType.WalkToNode ? current.NodeId : 0;
        var currentDistance = float.MaxValue;
        if (currentNode != 0 && memory.TryGetSpawnPoint(currentNode, out var target))
        {
            currentDistance = Vector3.Distance(player, target.Position);
        }

        var now = default(TreasureDetour);
        var append = default(TreasureDetour);
        var chests = memory.Chests;
        for (var i = 0; i < chests.Count; i++)
        {
            var chest = chests[i];
            var node = chest.Node;
            if (node == 0 || node == currentNode || !allowed.Contains(node) || attempted.Contains(node) || memory.IsUnreachable(node) || TreasureData.IsAvoided(node))
            {
                continue;
            }

            var distance = Vector3.Distance(player, chest.Position);
            var ahead = HuntStepRules.IsAhead(steps, index, node);
            if (walking && distance <= range && (distance < currentDistance || !ahead))
            {
                if (now.Kind == TreasureDetourKind.None || distance < now.Distance)
                {
                    now = new TreasureDetour(TreasureDetourKind.Now, chest, distance);
                }
            }
            else if (!ahead && append.Kind == TreasureDetourKind.None)
            {
                append = new TreasureDetour(TreasureDetourKind.Append, chest, distance);
            }
        }

        return now.Kind != TreasureDetourKind.None ? now : append;
    }
}
