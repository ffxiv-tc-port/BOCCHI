using System;
using System.Collections.Generic;

namespace BOCCHI.Pathfinding;

/// <summary>
/// 巡迴步驟清單的跳過規則。清單由一段一段組成：每段是 0～3 個交通步驟（走到乙太之晶、傳送、返回）接一個 WalkToNode。
/// 純邏輯、不碰遊戲、不配置；只在框架執行緒用。
/// </summary>
public static class HuntStepRules
{
    /// <returns>從 <paramref name="start"/> 起第一個 WalkToNode 的索引；沒有回 -1。</returns>
    public static int SegmentTarget(List<PathfinderStep> steps, int start)
    {
        for (var i = Math.Max(start, 0); i < steps.Count; i++)
        {
            if (steps[i].Type == PathfinderStepType.WalkToNode)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 目前這一步能不能因為「那個點已經確認過」跳過。WalkToNode 本身：確認過就跳（呼叫端要停下正在走的導航）。
    /// 一段的第一個交通步驟、還沒開始執行：目標確認過時，下一段也自帶交通（或沒有下一段）就整段跳掉；
    /// 下一段是直接走過去的，就保留交通步驟（下一個點在這個點附近），等走到 WalkToNode 再跳；
    /// 但剩下的點全都確認過（也沒有挖胡蘿蔔步驟）就直接跳到結尾，不為了一路跳過的點先傳送。
    /// 已經開始執行或在一段中間的交通步驟一律不動。
    /// </summary>
    /// <param name="started">目前這一步的處理器已經跑過至少一次。</param>
    /// <param name="next">跳過後的步驟索引。</param>
    /// <param name="node">被跳過的點。</param>
    public static bool TrySkip(List<PathfinderStep> steps, int index, bool started, Func<uint, bool> covered, out int next, out uint node)
    {
        next = index;
        node = 0;
        if (index < 0 || index >= steps.Count)
        {
            return false;
        }

        var step = steps[index];
        if (step.Type == PathfinderStepType.WalkToNode)
        {
            if (!covered(step.NodeId))
            {
                return false;
            }

            node = step.NodeId;
            next = index + 1;
            return true;
        }

        // 挖胡蘿蔔那一步不是交通步驟：不跟著整段跳掉。
        if (step.Type == PathfinderStepType.DigCarrot || started || (index > 0 && steps[index - 1].Type != PathfinderStepType.WalkToNode))
        {
            return false;
        }

        var target = SegmentTarget(steps, index);
        if (target < 0 || !covered(steps[target].NodeId))
        {
            return false;
        }

        var after = target + 1;
        if (after < steps.Count && steps[after].Type == PathfinderStepType.WalkToNode)
        {
            if (!RestCovered(steps, after, covered))
            {
                return false;
            }

            after = steps.Count;
        }

        node = steps[target].NodeId;
        next = after;
        return true;
    }

    // 從 start 起每個 WalkToNode 都確認過、而且沒有挖胡蘿蔔步驟。
    private static bool RestCovered(List<PathfinderStep> steps, int start, Func<uint, bool> covered)
    {
        for (var i = start; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Type == PathfinderStepType.DigCarrot || (step.Type == PathfinderStepType.WalkToNode && !covered(step.NodeId)))
            {
                return false;
            }
        }

        return true;
    }

    /// <returns><paramref name="index"/> 之後（含）還有沒有走向 <paramref name="node"/> 的步驟。</returns>
    public static bool IsAhead(List<PathfinderStep> steps, int index, uint node)
    {
        for (var i = Math.Max(index, 0); i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Type == PathfinderStepType.WalkToNode && step.NodeId == node)
            {
                return true;
            }
        }

        return false;
    }
}
