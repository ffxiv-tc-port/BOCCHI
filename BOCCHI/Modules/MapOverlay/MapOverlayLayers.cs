using System;
using System.Collections.Generic;
using System.Numerics;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.Treasure;
using BOCCHI.Pathfinding;

namespace BOCCHI.Modules.MapOverlay;

// 本檔契約：純邏輯，不碰遊戲、不呼叫 IPC；只在框架執行緒、每秒最多一次被呼叫，每次新配置推送用的陣列（給 Mappy 的不可重用）。
// 顏色是 0xRRGGBB；Mappy 只取 RGB。點數與每個說明的長度照 Mappy.Overlay 契約限制在 MaxPoints／MaxLabelLength。

public enum OverlayShape
{
    Path,
    Points,
}

/// <summary>一個 Mappy 疊加層（同一個 source＋key 覆寫）。Labels 是 null 或長度等於點數。</summary>
public sealed record OverlayLayer(string Key, OverlayShape Shape, Vector3[] Points, uint Rgb, int Flags, string[]? Labels);

/// <summary>Mappy.Overlay 的 flags：低 2 位是狀態，其餘是位元。</summary>
public static class OverlayFlags
{
    public const int Confirmed = 0;

    public const int Possible = 1;

    public const int Done = 2;

    public const int Arrows = 0x10;

    public const int Numbers = 0x20;
}

public static class MapOverlayLayers
{
    public const int MaxPoints = 2048;

    public const int MaxLabelLength = 128;

    /// <summary>看到的幸運胡蘿蔔離生成點這麼近就算在那個點上。</summary>
    public const float CarrotMatchM = 5f;

    /// <summary>依 <see cref="TreasureRoutes.Order"/>（同分段地圖）。</summary>
    public static readonly uint[] RouteColors =
    [
        Rgb(214, 39, 40),
        Rgb(31, 119, 180),
        Rgb(255, 127, 14),
        Rgb(148, 103, 189),
        Rgb(44, 160, 44),
    ];

    public static readonly uint NoRouteColor = Rgb(200, 200, 200);

    public static readonly uint BronzeColor = Rgb(205, 127, 50);

    public static readonly uint SilverColor = Rgb(192, 192, 192);

    public static readonly uint VisitedColor = Rgb(128, 128, 128);

    public static readonly uint CarrotColor = Rgb(255, 215, 0);

    public static readonly uint DestinationColor = Rgb(255, 255, 255);

    public static readonly uint PotColor = Rgb(227, 119, 194);

    public static uint Rgb(byte r, byte g, byte b)
    {
        return (uint)((r << 16) | (g << 8) | b);
    }

    /// <summary>生成點所屬的段（<see cref="TreasureRoutes.Order"/> 的索引）；不在表裡回 -1。</summary>
    public static int RouteIndex(uint node)
    {
        return TreasureRoutes.Nodes.TryGetValue(node, out var route) ? TreasureRoutes.IndexOf(route) : -1;
    }

    public static uint RouteColor(int route)
    {
        return route >= 0 && route < RouteColors.Length ? RouteColors[route] : NoRouteColor;
    }

    public static string Clip(string label)
    {
        return label.Length <= MaxLabelLength ? label : label[..(MaxLabelLength - 1)] + "…";
    }

    public delegate bool NodePosition(uint node, out Vector3 position);

    private sealed class Piece(int route)
    {
        public int Route = route;

        public bool HasNode;

        public readonly List<Vector3> Points = [];

        public readonly List<string> Labels = [];

        public void Add(Vector3 point, string label)
        {
            Points.Add(point);
            Labels.Add(Clip(label));
        }
    }

    /// <summary>
    /// 寶箱巡迴從 <paramref name="start"/> 起還沒走的步驟：一段一個 key（plan.0、plan.1…），淡色直線＋序號、照段上色。
    /// 傳送與返回斷開（新的一段從落點起）；走到另一段的點時也換一個 key，但從上一段最後一點接上、線不斷。
    /// 站號從整個規劃的第一個點起算（已經走過的也算），只寫在說明裡。查不到位置的點略過。
    /// </summary>
    public static void BuildPlan(IReadOnlyList<PathfinderStep> steps, int start, NodePosition nodePosition, MapOverlayTexts texts, List<OverlayLayer> into)
    {
        var station = 0;
        for (var i = 0; i < start && i < steps.Count; i++)
        {
            if (steps[i].Type == PathfinderStepType.WalkToNode)
            {
                station++;
            }
        }

        var pieces = new List<Piece>();
        Piece? current = null;
        for (var i = Math.Max(start, 0); i < steps.Count; i++)
        {
            var step = steps[i];
            switch (step.Type)
            {
                case PathfinderStepType.WalkToNode:
                {
                    station++;
                    if (!nodePosition(step.NodeId, out var position))
                    {
                        continue;
                    }

                    var route = RouteIndex(step.NodeId);
                    if (current is { HasNode: true } && current.Route != route)
                    {
                        var joinAt = current.Points[^1];
                        var joinLabel = current.Labels[^1];
                        pieces.Add(current);
                        current = new Piece(route);
                        current.Add(joinAt, joinLabel);
                    }

                    current ??= new Piece(route);
                    if (!current.HasNode)
                    {
                        current.Route = route;
                    }

                    current.HasNode = true;
                    current.Add(position, texts.PlanNode(route, station, step.NodeId));
                    break;
                }
                case PathfinderStepType.WalkToAethernet:
                    current ??= new Piece(-1);
                    current.Add(step.Aethernet.GetData().Position, texts.PlanShard(step.Aethernet));
                    break;
                case PathfinderStepType.TeleportToAethernet:
                    Close(pieces, current);
                    current = new Piece(-1);
                    current.Add(step.Aethernet.GetData().Destination, texts.PlanTeleport(step.Aethernet));
                    break;
                case PathfinderStepType.ReturnToBaseCamp:
                    Close(pieces, current);
                    current = new Piece(-1);
                    current.Add(Aethernet.BaseCamp.GetData().Destination, texts.PlanReturn);
                    break;
            }
        }

        Close(pieces, current);
        for (var i = 0; i < pieces.Count; i++)
        {
            var piece = pieces[i];
            var count = Math.Min(piece.Points.Count, MaxPoints);
            var points = piece.Points.GetRange(0, count).ToArray();
            var labels = piece.Labels.GetRange(0, count).ToArray();
            into.Add(new OverlayLayer($"plan.{i}", count >= 2 ? OverlayShape.Path : OverlayShape.Points, points, RouteColor(piece.Route),
                OverlayFlags.Possible | OverlayFlags.Numbers, labels));
        }
    }

    // 沒有任何生成點的一段（只有乙太之晶或落點）不畫。
    private static void Close(List<Piece> pieces, Piece? piece)
    {
        if (piece is { HasNode: true })
        {
            pieces.Add(piece);
        }
    }

    /// <summary>
    /// 寶箱生成點三態＋已記錄的寶箱：已記錄＝銅／銀實心（chest.bronze／chest.silver，含對不上生成點的）；
    /// 沒巡過＝段色淡色空心（spawn.open.0～4，不在分段表的是 spawn.open.other）；巡過沒有寶箱＝灰色小點（spawn.done，<paramref name="showVisited"/> 關就不畫）。
    /// </summary>
    public static void BuildSpawnPoints(IReadOnlyList<TreasureSpawnPoint> points, TreasureHuntMemory memory, bool showVisited, MapOverlayTexts texts, List<OverlayLayer> into)
    {
        var open = new List<Vector3>[RouteColors.Length + 1];
        var openLabels = new List<string>[open.Length];
        for (var i = 0; i < open.Length; i++)
        {
            open[i] = [];
            openLabels[i] = [];
        }

        var done = new List<Vector3>();
        var doneLabels = new List<string>();
        foreach (var point in points)
        {
            if (memory.HasChestAt(point.Id))
            {
                continue;
            }

            var route = RouteIndex(point.Id);
            if (memory.IsVisited(point.Id))
            {
                if (showVisited)
                {
                    done.Add(point.Position);
                    doneLabels.Add(Clip(texts.SpawnVisited(route, point.Id)));
                }

                continue;
            }

            var slot = route >= 0 && route < RouteColors.Length ? route : RouteColors.Length;
            open[slot].Add(point.Position);
            openLabels[slot].Add(Clip(texts.SpawnOpen(route, point.Id)));
        }

        var bronze = new List<Vector3>();
        var bronzeLabels = new List<string>();
        var silver = new List<Vector3>();
        var silverLabels = new List<string>();
        var chests = memory.Chests;
        for (var i = 0; i < chests.Count; i++)
        {
            var chest = chests[i];
            var label = Clip(texts.Chest(chest, RouteIndex(chest.Node), memory.IsUnreachable(chest.Node)));
            if (chest.Type == TreasureType.Silver)
            {
                silver.Add(chest.Position);
                silverLabels.Add(label);
            }
            else
            {
                bronze.Add(chest.Position);
                bronzeLabels.Add(label);
            }
        }

        AddPoints(into, "chest.bronze", bronze, bronzeLabels, BronzeColor, OverlayFlags.Confirmed);
        AddPoints(into, "chest.silver", silver, silverLabels, SilverColor, OverlayFlags.Confirmed);
        for (var i = 0; i < RouteColors.Length; i++)
        {
            AddPoints(into, $"spawn.open.{i}", open[i], openLabels[i], RouteColors[i], OverlayFlags.Possible);
        }

        AddPoints(into, "spawn.open.other", open[RouteColors.Length], openLabels[RouteColors.Length], NoRouteColor, OverlayFlags.Possible);
        AddPoints(into, "spawn.done", done, doneLabels, VisitedColor, OverlayFlags.Done);
    }

    /// <summary>
    /// 幸運胡蘿蔔巡迴進行中的生成點：看得到胡蘿蔔＝實心（carrot.seen）；這一輪還沒去＝淡色空心（carrot.open）；去過了＝灰色小點（carrot.done）。
    /// 已經規劃過時不在這一輪的點不畫；還沒規劃完（<see cref="HuntProgress.PlannedCount"/> 是 0）全部當還沒去。
    /// </summary>
    public static void BuildCarrots(IReadOnlyList<(uint Id, Vector3 Position, uint Level)> data, HuntProgress progress, IReadOnlyList<Vector3> seen, MapOverlayTexts texts, List<OverlayLayer> into)
    {
        var seenPoints = new List<Vector3>();
        var seenLabels = new List<string>();
        var open = new List<Vector3>();
        var openLabels = new List<string>();
        var done = new List<Vector3>();
        var doneLabels = new List<string>();
        var planned = progress.PlannedCount > 0;
        foreach (var (id, position, _) in data)
        {
            if (IsNear(seen, position))
            {
                seenPoints.Add(position);
                seenLabels.Add(Clip(texts.CarrotSeen(id)));
            }
            else if (progress.IsVisited(id))
            {
                done.Add(position);
                doneLabels.Add(Clip(texts.CarrotDone(id)));
            }
            else if (!planned || progress.IsPlanned(id))
            {
                open.Add(position);
                openLabels.Add(Clip(texts.CarrotOpen(id)));
            }
        }

        AddPoints(into, "carrot.seen", seenPoints, seenLabels, CarrotColor, OverlayFlags.Confirmed);
        AddPoints(into, "carrot.open", open, openLabels, CarrotColor, OverlayFlags.Possible);
        AddPoints(into, "carrot.done", done, doneLabels, VisitedColor, OverlayFlags.Done);
    }

    private static bool IsNear(IReadOnlyList<Vector3> seen, Vector3 position)
    {
        for (var i = 0; i < seen.Count; i++)
        {
            if (Vector3.DistanceSquared(seen[i], position) <= CarrotMatchM * CarrotMatchM)
            {
                return true;
            }
        }

        return false;
    }

    public static void BuildDestination(Vector3 destination, string label, List<OverlayLayer> into)
    {
        into.Add(new OverlayLayer("destination", OverlayShape.Points, [destination], DestinationColor, OverlayFlags.Confirmed, [Clip(label)]));
    }

    /// <summary>等魔法甕：出生點（pot.spawn）與選好的等待點（pot.wait；還沒選好不畫）。</summary>
    public static void BuildPotWait(Vector3 spawn, Vector3? hold, string spawnLabel, string holdLabel, List<OverlayLayer> into)
    {
        into.Add(new OverlayLayer("pot.spawn", OverlayShape.Points, [spawn], PotColor, OverlayFlags.Confirmed, [Clip(spawnLabel)]));
        if (hold is { } point)
        {
            into.Add(new OverlayLayer("pot.wait", OverlayShape.Points, [point], PotColor, OverlayFlags.Confirmed, [Clip(holdLabel)]));
        }
    }

    // 沒有點的層不加：呼叫端把上一輪推過、這一輪沒有的 key 拿掉。
    private static void AddPoints(List<OverlayLayer> into, string key, List<Vector3> points, List<string> labels, uint rgb, int flags)
    {
        if (points.Count == 0)
        {
            return;
        }

        var count = Math.Min(points.Count, MaxPoints);
        into.Add(new OverlayLayer(key, OverlayShape.Points, points.GetRange(0, count).ToArray(), rgb, flags, labels.GetRange(0, count).ToArray()));
    }
}
