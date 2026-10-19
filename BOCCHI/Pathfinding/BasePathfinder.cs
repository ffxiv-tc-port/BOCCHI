using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.Treasure;
using ECommons.DalamudServices;

namespace BOCCHI.Pathfinding;

/// <param name="chestFirstMaxSeconds">
/// 優先點（已記錄的寶箱）全部排在最前面、比跟其餘的點排成同一趟多花的時間不超過這麼多秒才先開；+∞＝一律先開（不比較）。
/// </param>
public abstract class BasePathfinder(float returnCost = 300f, float teleportCost = 50f, float chestFirstMaxSeconds = float.PositiveInfinity) : IPathfinder
{
    // 成本表可能在執行緒池上讀完：先寫 data 再寫這個 volatile 狀態，讀到 FileLoaded 的一方就看得到完整的 data。
    private volatile PathfinderState state = PathfinderState.None;

    public PathfinderState State
    {
        get => state;
        private set => state = value;
    }

    private NodeDataSchema data = new();

    /// <summary>用已經讀好的成本表（不讀檔），建好就可以規劃。</summary>
    protected BasePathfinder(NodeDataSchema data, float returnCost, float teleportCost) : this(returnCost, teleportCost)
    {
        this.data = data;
        State = PathfinderState.FileLoaded;
    }

    protected abstract uint GetStartingNode(Vector3 start, List<uint> nodes);

    public Task<List<PathfinderStep>> FindPath(Vector3 start, List<uint> nodes)
    {
        if (State != PathfinderState.FileLoaded)
        {
            throw new Exception("File not loaded");
        }

        State = PathfinderState.Pathfinding;

        var startNode = GetStartingNode(start, nodes);


        var graph = BuildCostGraph(nodes);
        var tally = new PolishTally();
        var ordered = tally.Use(Polish(SolveTSPNearestInsertion(startNode, nodes, graph), graph, null, tally));
        var steps = BuildStepPath(ordered, graph);

        steps.Insert(0, PathfinderStep.WalkToDestination(startNode));

        LogPolish(tally);
        PrintPath(steps);
        State = PathfinderState.PathfindingDone;
        return Task.FromResult(steps);
    }

    /// <summary>
    /// 兩段規劃：先從離起點最近的優先點出發排完全部優先點，再從最後一個優先點接著排其餘的點；兩段用同一張成本表。
    /// 沒有優先點、或全部都是優先點時與兩個參數的版本完全相同。
    /// </summary>
    public Task<List<PathfinderStep>> FindPath(Vector3 start, List<uint> nodes, IReadOnlyCollection<uint> priority)
    {
        var first = nodes.Where(priority.Contains).ToList();
        if (first.Count == 0 || first.Count == nodes.Count)
        {
            return FindPath(start, nodes);
        }

        if (State != PathfinderState.FileLoaded)
        {
            throw new Exception("File not loaded");
        }

        State = PathfinderState.Pathfinding;

        var startNode = GetStartingNode(start, first);
        var graph = BuildCostGraph(nodes);
        var ordered = SolveTSPNearestInsertion(startNode, first, graph);
        var last = ordered[^1];
        var rest = new List<uint> { last };
        rest.AddRange(nodes.Where(n => !priority.Contains(n)));
        ordered.AddRange(SolveTSPNearestInsertion(last, rest, graph).Skip(1));
        var tally = new PolishTally();
        var head = Polish(ordered, graph, PriorityGroups(priority), tally);
        ordered = tally.Use(head);
        if (ComparesPriority)
        {
            var together = Polish(SolveTSPNearestInsertion(startNode, nodes, graph), graph, null, tally);
            if (!KeepPriorityFirst(first.Count, head.After, together.After))
            {
                ordered = tally.Use(together);
            }
        }

        var steps = BuildStepPath(ordered, graph);
        steps.Insert(0, PathfinderStep.WalkToDestination(startNode));

        LogPolish(tally);
        PrintPath(steps);
        State = PathfinderState.PathfindingDone;
        return Task.FromResult(steps);
    }

    /// <summary>最近一次四個參數的規劃選的第一段；沒有優先點時是 null。只在 framework 執行緒讀。</summary>
    public StartLegChoice? LastStartLeg { get; private set; }

    /// <summary>
    /// 同三個參數的版本，但第一段（角色目前位置到第一個優先點）與其他段一樣比步行／走到乙太之晶傳送／返回再傳送，
    /// 第一個優先點選「第一段＋排完全部優先點」成本最低的；步行路長取 <paramref name="startLeg"/>。沒有優先點時與兩個參數的版本完全相同。
    /// </summary>
    public Task<List<PathfinderStep>> FindPath(Vector3 start, List<uint> nodes, IReadOnlyCollection<uint> priority, StartLegCosts startLeg)
    {
        var first = nodes.FindAll(n => priority.Contains(n));
        if (first.Count == 0)
        {
            LastStartLeg = null;
            return FindPath(start, nodes);
        }

        if (State != PathfinderState.FileLoaded)
        {
            throw new Exception("File not loaded");
        }

        State = PathfinderState.Pathfinding;

        var graph = BuildCostGraph(nodes);
        var choice = ChooseStartLeg(first, first, graph, startLeg, out _);
        var ordered = SolveTSPNearestInsertion(choice.Node, first, graph);
        if (first.Count < nodes.Count)
        {
            var last = ordered[^1];
            var rest = new List<uint> { last };
            rest.AddRange(nodes.Where(n => !priority.Contains(n)));
            ordered.AddRange(SolveTSPNearestInsertion(last, rest, graph).Skip(1));
        }

        // 局部搜尋只做選定的路線（第一站已選好）：對每個候選第一站都做太慢。
        var tally = new PolishTally();
        var head = Polish(ordered, graph, first.Count < nodes.Count ? PriorityGroups(priority) : null, tally);
        ordered = tally.Use(head);
        choice = choice with { TourCost = TourCost(ordered.GetRange(0, first.Count), graph) };
        var together = false;
        if (ComparesPriority && first.Count < nodes.Count)
        {
            // 同一趟的第一站也只從已記錄的寶箱裡挑：第一段的步行路長只量了它們。
            var round = ChooseStartLeg(first, nodes, graph, startLeg, out var roundOrder);
            var merged = Polish(roundOrder, graph, null, tally);
            if (!KeepPriorityFirst(first.Count, choice.LegCost + head.After, round.LegCost + merged.After))
            {
                choice = round with { TourCost = merged.After };
                ordered = tally.Use(merged);
                together = true;
            }
        }

        var steps = BuildStepPath(ordered, graph);
        steps.InsertRange(0, choice.Steps);
        LastStartLeg = choice;

        Svc.Log.Information(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[BOCCHI][尋路] 第一段（已記錄的寶箱 {first.Count} 個{(together ? "跟生成點排成同一趟" : "先開")}）：先去生成點 {choice.Node}，{DescribeLeg(choice)}，成本 {choice.LegCost:F0}"
            + $"（之後排完{(together ? string.Empty : "已記錄的寶箱")} {choice.TourCost:F0}）；直接步行 {DescribeWalk(choice.Walk)}；返回 {returnCost:F0}、傳送 {teleportCost:F0}"));

        LogPolish(tally);
        PrintPath(steps);
        State = PathfinderState.PathfindingDone;
        return Task.FromResult(steps);
    }

    /// <summary>
    /// 沒有優先點、只巡部分路線時：第一站與走法照四個參數的版本選（候選＝全部要跑的點，比步行／走到乙太之晶傳送／返回再傳送），
    /// 其餘照成本表從第一站排。<paramref name="startLeg"/> 沒量到的點用直線。
    /// </summary>
    public Task<List<PathfinderStep>> FindPathFromStartLeg(Vector3 start, List<uint> nodes, StartLegCosts startLeg)
    {
        if (State != PathfinderState.FileLoaded)
        {
            throw new Exception("File not loaded");
        }

        State = PathfinderState.Pathfinding;
        var graph = BuildCostGraph(nodes);
        var choice = ChooseStartLeg(nodes, nodes, graph, startLeg, out var order);
        var tally = new PolishTally();
        var polished = Polish(order, graph, null, tally);
        order = tally.Use(polished);
        choice = choice with { TourCost = polished.After };
        var steps = BuildStepPath(order, graph);
        steps.InsertRange(0, choice.Steps);
        LastStartLeg = choice;

        Svc.Log.Information(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[BOCCHI][尋路] 第一段（只巡所選路線，{nodes.Count} 個點）：先去生成點 {choice.Node}，{DescribeLeg(choice)}，成本 {choice.LegCost:F0}（之後排完 {choice.TourCost:F0}）；"
            + $"直接步行 {DescribeWalk(choice.Walk)}；返回 {returnCost:F0}、傳送 {teleportCost:F0}"));

        LogPolish(tally);
        PrintPath(steps);
        State = PathfinderState.PathfindingDone;
        return Task.FromResult(steps);
    }

    /// <summary>
    /// 從乙太之晶 <paramref name="entry"/> 的傳送落點出發、照成本表走完 <paramref name="nodes"/> 的成本（不含到那個乙太之晶之前）：
    /// 第一站取「走過去＋之後排完全部」最便宜的點。沒有點回 0；成本表沒有這個乙太之晶到任何一點的距離時回 +∞。不改 <see cref="State"/>。
    /// </summary>
    public float EstimateRound(Aethernet entry, List<uint> nodes)
    {
        if (nodes.Count == 0)
        {
            return 0f;
        }

        if (!data.AethernetToNodeDistances.TryGetValue(entry, out var fromEntry))
        {
            return float.PositiveInfinity;
        }

        var graph = BuildCostGraph(nodes);
        var best = float.PositiveInfinity;
        var bestFirst = 0f;
        List<uint>? bestOrder = null;
        foreach (var node in nodes)
        {
            var first = fromEntry.FirstOrDefault(x => x.Id == node);
            if (first.Id != node)
            {
                continue;
            }

            var order = SolveTSPNearestInsertion(node, nodes, graph);
            var total = first.Distance + TourCost(order, graph);
            if (total < best)
            {
                best = total;
                bestFirst = first.Distance;
                bestOrder = order;
            }
        }

        // 同實際規劃：只對選定的第一站做局部搜尋。
        return bestOrder == null ? best : bestFirst + Polish(bestOrder, graph, null, new PolishTally()).After;
    }

    /// <summary>沒有優先點時逆向的第一站＝兩個參數的版本排好之後（含局部搜尋）的最後一站；給規劃前量第一段的步行路長。</summary>
    public uint ReverseStartNode(Vector3 start, List<uint> nodes)
    {
        var graph = BuildCostGraph(nodes);
        return Polish(SolveTSPNearestInsertion(GetStartingNode(start, nodes), nodes, graph), graph, null, new PolishTally()).Route[^1];
    }

    /// <summary>
    /// 逆向＝先照正向排好（含局部搜尋），把要倒的那一段倒過來再整理：那一段的第一站（正向的最後一站）不動、不會被整理回正向（見 <see cref="ReverseGroups"/>）。
    /// 沒有優先點：整條倒過來；第一段（角色目前位置到 <see cref="ReverseStartNode"/>）照 <paramref name="startLeg"/> 比步行／走到乙太之晶傳送／返回再傳送
    /// （null 或沒量那個點＝直接步行）。有優先點：只倒優先點之後的那一段，第一段與正向相同
    /// （<paramref name="startLeg"/> 是 null＝三個參數的版本、否則四個參數的版本）。
    /// </summary>
    public Task<List<PathfinderStep>> FindPathReversed(Vector3 start, List<uint> nodes, IReadOnlyCollection<uint> priority, StartLegCosts? startLeg)
    {
        if (State != PathfinderState.FileLoaded)
        {
            throw new Exception("File not loaded");
        }

        State = PathfinderState.Pathfinding;
        var graph = BuildCostGraph(nodes);
        var first = nodes.FindAll(n => priority.Contains(n));
        List<uint> ordered;
        List<PathfinderStep> lead;
        LastStartLeg = null;
        var tally = new PolishTally();
        if (first.Count == 0)
        {
            var forward = Polish(SolveTSPNearestInsertion(GetStartingNode(start, nodes), nodes, graph), graph, null, tally).Route;
            ordered = TurnAfter(forward, 0, out var forwardStart);
            ordered = tally.Use(Polish(ordered, graph, ReverseGroups(ordered, 0), tally));
            tally.Turned = true;
            Svc.Log.Information($"[BOCCHI][尋路] 逆向排法：全部 {ordered.Count} 個點從生成點 {ordered[0]}（正向規劃的另一端）開始倒過來走，"
                                + $"最後一站是生成點 {ordered[^1]}；正向規劃從生成點 {forwardStart} 開始");
            lead = [PathfinderStep.WalkToDestination(ordered[0])];
            if (startLeg != null && startLeg.Nodes.TryGetValue(ordered[0], out var walk))
            {
                var choice = BestStartLeg(ordered[0], walk, startLeg);
                lead = choice.Steps;
                LastStartLeg = choice;
                Svc.Log.Information(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"[BOCCHI][尋路] 第一段（逆向，從另一端開始）：先去生成點 {choice.Node}，{DescribeLeg(choice)}，成本 {choice.LegCost:F0}；"
                    + $"直接步行 {DescribeWalk(choice.Walk)}；返回 {returnCost:F0}、傳送 {teleportCost:F0}"));
            }
        }
        else
        {
            uint startNode;
            StartLegChoice? choice = null;
            if (startLeg != null)
            {
                choice = ChooseStartLeg(first, first, graph, startLeg, out _);
                startNode = choice.Node;
                lead = choice.Steps;
                LastStartLeg = choice;
            }
            else
            {
                startNode = GetStartingNode(start, first);
                lead = [PathfinderStep.WalkToDestination(startNode)];
            }

            // 正向規劃在倒過來那一段的第一站（只給記錄行對照）。
            uint nearEnd = 0;
            Func<uint, int>? groups = null;
            ordered = SolveTSPNearestInsertion(startNode, first, graph);
            if (first.Count < nodes.Count)
            {
                var last = ordered[^1];
                var rest = new List<uint> { last };
                rest.AddRange(nodes.Where(n => !priority.Contains(n)));
                ordered.AddRange(SolveTSPNearestInsertion(last, rest, graph).Skip(1));

                // 先照正向排好（同三個參數的版本先開時的路線），再把已記錄寶箱之後的那一段倒過來。
                ordered = TurnAfter(Polish(ordered, graph, PriorityGroups(priority), tally).Route, first.Count, out nearEnd);
                groups = ReverseGroups(ordered, first.Count);
                tally.Turned = true;
            }

            var head = Polish(ordered, graph, groups, tally);
            ordered = tally.Use(head);

            // 同一趟的逆向＝從同一個第一站出發的正向同一趟排好之後，第一站之後倒過來走。
            var together = false;
            if (ComparesPriority && first.Count < nodes.Count)
            {
                var round = TurnAfter(Polish(SolveTSPNearestInsertion(startNode, nodes, graph), graph, null, tally).Route, 1, out var roundNear);
                var merged = Polish(round, graph, ReverseGroups(round, 1), tally);
                if (!KeepPriorityFirst(first.Count, head.After, merged.After))
                {
                    ordered = tally.Use(merged);
                    together = true;
                    nearEnd = roundNear;
                }
            }

            if (first.Count < nodes.Count)
            {
                var from = together ? 1 : first.Count;
                Svc.Log.Information($"[BOCCHI][尋路] 逆向排法：{(together ? "第一站" : "已記錄的寶箱")}之後的 {ordered.Count - from} 個點從生成點 {ordered[from]}（正向規劃的另一端）開始倒過來走，"
                                    + $"最後一站是生成點 {ordered[^1]}；正向規劃這一段從生成點 {nearEnd} 開始");
            }

            if (choice != null)
            {
                Svc.Log.Information(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"[BOCCHI][尋路] 第一段（已記錄的寶箱 {first.Count} 個{(together ? "跟生成點排成同一趟" : "先開")}）：先去生成點 {choice.Node}，{DescribeLeg(choice)}，成本 {choice.LegCost:F0}"
                    + $"（之後排完{(together ? string.Empty : "已記錄的寶箱")} {TourCost(together ? ordered : ordered.GetRange(0, first.Count), graph):F0}）；直接步行 {DescribeWalk(choice.Walk)}；返回 {returnCost:F0}、傳送 {teleportCost:F0}"));
            }
        }

        var steps = BuildStepPath(ordered, graph);
        steps.InsertRange(0, lead);
        LogPolish(tally);
        PrintPath(steps);
        State = PathfinderState.PathfindingDone;
        return Task.FromResult(steps);
    }

    // 第一站從 candidates 挑、之後排完 tourNodes；總成本同分時取第一段直線較近的。
    private StartLegChoice ChooseStartLeg(List<uint> candidates, List<uint> tourNodes, Dictionary<uint, Dictionary<uint, (float Cost, List<PathfinderStep> Steps)>> graph,
        StartLegCosts costs, out List<uint> bestOrder)
    {
        StartLegChoice? best = null;
        bestOrder = [];
        var bestTotal = float.PositiveInfinity;
        foreach (var node in candidates)
        {
            var walk = costs.Nodes.TryGetValue(node, out var known) ? known : new StartWalk(float.PositiveInfinity, StartWalkSource.Straight, float.PositiveInfinity);
            var leg = BestStartLeg(node, walk, costs);
            var order = SolveTSPNearestInsertion(node, tourNodes, graph);
            var tour = TourCost(order, graph);
            var total = leg.LegCost + tour;
            var better = best == null || total < bestTotal || (total == bestTotal && walk.Straight < best.Walk.Straight);
            if (better)
            {
                best = leg with { TourCost = tour };
                bestOrder = order;
                bestTotal = total;
            }
        }

        return best!;
    }

    private bool ComparesPriority
    {
        get => !float.IsPositiveInfinity(chestFirstMaxSeconds);
    }

    // 寫一行 Information；回 true＝照舊先開。兩邊的第一段相同時呼叫端只傳之後的路線成本。
    private bool KeepPriorityFirst(int count, float firstCost, float togetherCost)
    {
        var extra = (firstCost - togetherCost) / TreasureRouteStats.MetersPerSecond;
        var keep = extra <= chestFirstMaxSeconds;
        Svc.Log.Information(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[BOCCHI][寶箱記錄] 已記錄的寶箱 {count} 個：全部排在最前面先開的成本 {firstCost:F0}、跟生成點排成同一趟 {togetherCost:F0}，"
            + $"先開多花約 {extra:F0} 秒（上限 {chestFirstMaxSeconds:F0} 秒）⇒ {(keep ? "照舊先開" : "排成同一趟")}"));
        return keep;
    }

    // 選法同 GetBestSteps（同分取步行），只是起點不是生成點：步行路長來自 costs，不查成本表。
    private StartLegChoice BestStartLeg(uint node, StartWalk walk, StartLegCosts costs)
    {
        var best = new StartLegChoice(node, StartLegKind.Walk, walk.Reachable ? walk.Meters : float.PositiveInfinity, 0, Aethernet.BaseCamp, Aethernet.BaseCamp, walk, default,
            [PathfinderStep.WalkToDestination(node)]);

        foreach (var (shard, toShard) in costs.Shards)
        {
            if (!toShard.Reachable)
            {
                continue;
            }

            foreach (var (aethernet, list) in data.AethernetToNodeDistances)
            {
                var to = list.FirstOrDefault(x => x.Id == node);
                if (aethernet == shard || to.Id != node)
                {
                    continue;
                }

                var cost = toShard.Meters + teleportCost + to.Distance;
                if (cost < best.LegCost)
                {
                    best = best with
                    {
                        Kind = StartLegKind.ShardTeleport, LegCost = cost, From = shard, To = aethernet, ShardWalk = toShard,
                        Steps = [PathfinderStep.WalkToAethernet(shard), PathfinderStep.TeleportToAethernet(aethernet), PathfinderStep.WalkToDestination(node)],
                    };
                }
            }
        }

        foreach (var (aethernet, list) in data.AethernetToNodeDistances)
        {
            var to = list.FirstOrDefault(x => x.Id == node);
            if (to.Id != node)
            {
                continue;
            }

            var cost = returnCost + teleportCost + to.Distance;
            if (cost < best.LegCost)
            {
                best = best with
                {
                    Kind = StartLegKind.ReturnTeleport, LegCost = cost, From = Aethernet.BaseCamp, To = aethernet, ShardWalk = default,
                    Steps = aethernet == Aethernet.BaseCamp
                        ? [PathfinderStep.ReturnToBaseCamp(), PathfinderStep.WalkToDestination(node)]
                        : [PathfinderStep.ReturnToBaseCamp(), PathfinderStep.TeleportToAethernet(aethernet), PathfinderStep.WalkToDestination(node)],
                };
            }
        }

        return best;
    }

    private static float TourCost(List<uint> route, Dictionary<uint, Dictionary<uint, (float Cost, List<PathfinderStep> Steps)>> graph)
    {
        var total = 0f;
        for (var i = 0; i < route.Count - 1; i++)
        {
            total += graph[route[i]][route[i + 1]].Cost;
        }

        return total;
    }

    /// <summary>局部搜尋一條路線最多改幾步、跑幾毫秒，先到先停（停在當下的路線）；規劃在框架執行緒上。</summary>
    private const int PolishMaxMoves = 300;

    private const long PolishMaxMs = 15;

    // 一步至少省這麼多米才改：浮點誤差不會讓它來回搬。
    private const double PolishMinGain = 0.5;

    private readonly record struct Polished(List<uint> Route, float Before, float After);

    // 一次規劃裡全部局部搜尋的步數與耗時；Before／After 是最後選用那條路線的。
    private sealed class PolishTally
    {
        public int Runs;

        public int Moves;

        public double Ms;

        public bool Capped;

        // 選用的路線是「照正向排好再倒過來」之後才做最後一次局部搜尋的（Before 是倒過來之後的成本）。
        public bool Turned;

        public float Before;

        public float After;

        public List<uint> Use(Polished polished)
        {
            Before = polished.Before;
            After = polished.After;
            return polished.Route;
        }
    }

    // 先開的已記錄寶箱一組、其餘一組：局部搜尋只在組內搬，先開的仍然全部排在最前面。
    private static Func<uint, int> PriorityGroups(IReadOnlyCollection<uint> priority)
    {
        return node => priority.Contains(node) ? 0 : 1;
    }

    // 複本：route[from..] 倒過來；near＝倒過來之前那一段的第一站。
    private static List<uint> TurnAfter(List<uint> route, int from, out uint near)
    {
        var turned = new List<uint>(route);
        near = turned[from];
        turned.Reverse(from, turned.Count - from);
        return turned;
    }

    // 逆向倒過來的那一段在局部搜尋裡的組號：2-opt 在這一組一次最多倒三分之一（至少 2 個點），整理得了局部、轉不回正向。
    private const int ReversedGroup = 2;

    // 逆向：route[pinned] 是倒過來那一段的第一站（正向規劃的另一端），自成一組＝局部搜尋搬不動它；
    // 它前面（先開的已記錄寶箱或同一趟的第一站）一組、後面是 ReversedGroup。少了這兩道，那一段會被 2-opt 倒回正向。
    private static Func<uint, int> ReverseGroups(List<uint> route, int pinned)
    {
        var before = new HashSet<uint>(route.GetRange(0, pinned));
        var anchor = route[pinned];
        return node => node == anchor ? 1 : before.Contains(node) ? 0 : ReversedGroup;
    }

    /// <summary>
    /// 最近插入之後的局部搜尋：每一步在「搬 1～3 個相連的點到別處」（Or-opt）與「把一段倒過來走」（2-opt）裡取省最多的，
    /// 直到沒有省 <see cref="PolishMinGain"/> 米以上的一步或到上限。第一個點不動；<paramref name="groupOf"/> 不是 null 時
    /// 點只在同組的範圍裡搬、倒過來的一段必須整段同組（組在路線上必須各自相連）。成本表兩個方向不對稱：倒過來的那一段逐段用反方向重新計價。
    /// <see cref="ReversedGroup"/> 那一組 2-opt 一次倒的點數有上限（沒有這一組的路線不受影響）。
    /// </summary>
    private static Polished Polish(List<uint> route, Dictionary<uint, Dictionary<uint, (float Cost, List<PathfinderStep> Steps)>> graph, Func<uint, int>? groupOf,
        PolishTally tally)
    {
        var before = TourCost(route, graph);
        var n = route.Count;
        if (n < 3)
        {
            return new Polished(route, before, before);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var cost = new double[n * n];
        var group = new int[n];
        var order = new int[n];
        var reversedCount = 0;
        for (var a = 0; a < n; a++)
        {
            order[a] = a;
            group[a] = groupOf?.Invoke(route[a]) ?? 0;
            reversedCount += group[a] == ReversedGroup ? 1 : 0;
            var row = graph[route[a]];
            for (var b = 0; b < n; b++)
            {
                cost[a * n + b] = a == b ? 0 : row[route[b]].Cost;
            }
        }

        var reversedFlipMax = Math.Max(2, reversedCount / 3);

        var moves = 0;
        var capped = false;
        var next = new int[n];
        while (true)
        {
            if (moves >= PolishMaxMoves || watch.ElapsedMilliseconds >= PolishMaxMs)
            {
                capped = true;
                break;
            }

            var best = -PolishMinGain;
            var kind = 0;
            int bestI = 0, bestJ = 0, bestLength = 0;

            // Or-opt：order[i..i+length) 搬到 order[j] 與 order[j+1] 之間（j 在最後＝接在最後面）。
            for (var length = 1; length <= 3; length++)
            {
                for (var i = 1; i + length <= n; i++)
                {
                    var first = order[i];
                    var last = order[i + length - 1];
                    var g = group[first];
                    if (group[last] != g)
                    {
                        continue;
                    }

                    var prev = order[i - 1];
                    var hasAfter = i + length < n;
                    var after = hasAfter ? order[i + length] : -1;
                    var removed = cost[prev * n + first] + (hasAfter ? cost[last * n + after] - cost[prev * n + after] : 0);
                    for (var j = 0; j < n; j++)
                    {
                        if (j >= i - 1 && j <= i + length - 1)
                        {
                            continue;
                        }

                        var u = order[j];
                        var hasV = j + 1 < n;
                        var v = hasV ? order[j + 1] : -1;
                        if (group[u] != g && !(hasV && group[v] == g))
                        {
                            continue;
                        }

                        var delta = cost[u * n + first] + (hasV ? cost[last * n + v] - cost[u * n + v] : 0) - removed;
                        if (delta < best)
                        {
                            best = delta;
                            kind = 1;
                            bestI = i;
                            bestJ = j;
                            bestLength = length;
                        }
                    }
                }
            }

            // 2-opt：order[i..k] 倒過來走；段內逐段換成反方向的成本。
            for (var i = 1; i < n - 1; i++)
            {
                var prev = order[i - 1];
                var head = order[i];
                var forward = 0.0;
                var backward = 0.0;
                for (var k = i + 1; k < n; k++)
                {
                    if (group[order[k]] != group[head] || (group[head] == ReversedGroup && k - i + 1 > reversedFlipMax))
                    {
                        break;
                    }

                    forward += cost[order[k - 1] * n + order[k]];
                    backward += cost[order[k] * n + order[k - 1]];
                    var tail = order[k];
                    var hasAfter = k + 1 < n;
                    var after = hasAfter ? order[k + 1] : -1;
                    var delta = cost[prev * n + tail] - cost[prev * n + head] + (hasAfter ? cost[head * n + after] - cost[tail * n + after] : 0) + backward - forward;
                    if (delta < best)
                    {
                        best = delta;
                        kind = 2;
                        bestI = i;
                        bestJ = k;
                    }
                }
            }

            if (kind == 0)
            {
                break;
            }

            if (kind == 2)
            {
                Array.Reverse(order, bestI, bestJ - bestI + 1);
            }
            else
            {
                var w = 0;
                for (var t = 0; t < n; t++)
                {
                    if (t >= bestI && t < bestI + bestLength)
                    {
                        continue;
                    }

                    next[w++] = order[t];
                    if (t == bestJ)
                    {
                        Array.Copy(order, bestI, next, w, bestLength);
                        w += bestLength;
                    }
                }

                (order, next) = (next, order);
            }

            moves++;
        }

        var result = new List<uint>(n);
        foreach (var index in order)
        {
            result.Add(route[index]);
        }

        var polishedCost = TourCost(result, graph);
        tally.Runs++;
        tally.Moves += moves;
        tally.Ms += watch.Elapsed.TotalMilliseconds;
        tally.Capped |= capped;
        return polishedCost <= before ? new Polished(result, before, polishedCost) : new Polished(route, before, before);
    }

    private static void LogPolish(PolishTally tally)
    {
        if (tally.Runs == 0)
        {
            return;
        }

        Svc.Log.Information(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[BOCCHI][尋路] 局部搜尋：選用的路線{(tally.Turned ? "照正向排好再倒過來" : "最近插入法排")}的成本 {tally.Before:F0} → {tally.After:F0}（少約 {(tally.Before - tally.After) / TreasureRouteStats.MetersPerSecond:F0} 秒）；"
            + $"這次規劃搜尋 {tally.Runs} 條路線、共改 {tally.Moves} 步、{tally.Ms:F1} 毫秒{(tally.Capped ? "，有路線到上限先停" : string.Empty)}"));
    }

    private static string DescribeLeg(StartLegChoice choice)
    {
        return choice.Kind switch
        {
            StartLegKind.ShardTeleport => $"走法＝走到乙太之晶 {choice.From}（{DescribeWalk(choice.ShardWalk)}）傳送到 {choice.To}",
            StartLegKind.ReturnTeleport => choice.To == Aethernet.BaseCamp ? "走法＝返回營地" : $"走法＝返回營地再傳送到 {choice.To}",
            _ => "走法＝步行",
        };
    }

    private static string DescribeWalk(StartWalk walk)
    {
        return walk.Source switch
        {
            StartWalkSource.Measured => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{walk.Meters:F0} 米（vnavmesh 量，直線 {walk.Straight:F0}）"),
            StartWalkSource.Unreachable => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"走不到（vnavmesh，直線 {walk.Straight:F0}）"),
            _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{walk.Meters:F0} 米（量不到，用直線）"),
        };
    }

    protected (float Cost, List<PathfinderStep> Steps) GetBestSteps(uint fromId, uint toId)
    {
        var bestCost = float.MaxValue;
        List<PathfinderStep> bestSteps = [];

        if (data.NodeToNodeDistances.TryGetValue(fromId, out var directList))
        {
            var direct = directList.FirstOrDefault(x => x.Id == toId);
            if (direct.Id == toId)
            {
                bestCost = direct.Distance;
                bestSteps = [PathfinderStep.WalkToDestination(toId)];
            }
        }

        // fromId 來自呼叫端的點清單（CarrotData.Data／TreasureData.Levels，寫死在 C#），這兩張字典來自手動重新產生的 Data/<zone>/precomputed_*_hunt_data.json：
        // 加了點卻沒重新產生 JSON 時兩邊的鍵會對不上。上面的 NodeToNodeDistances 已經用 TryGetValue 讀同一個鍵，這裡照樣防，
        // 對不上就落到下面返回營地的分支，不從尋路動作鏈擲出去。
        if (data.NodeToAethernetDistances.TryGetValue(fromId, out var aethernetList) && aethernetList.Count > 0)
        {
            var fromShard = aethernetList.OrderBy(x => x.Distance).FirstOrDefault();
            foreach (var (aethernet, list) in data.AethernetToNodeDistances)
            {
                var to = list.FirstOrDefault(x => x.Id == toId);
                if (to.Id != toId)
                {
                    continue;
                }

                var cost = fromShard.Distance + teleportCost + to.Distance;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestSteps =
                    [
                        PathfinderStep.WalkToAethernet(fromShard.Aethernet),
                        PathfinderStep.TeleportToAethernet(aethernet),
                        PathfinderStep.WalkToDestination(toId),
                    ];
                }
            }
        }

        foreach (var (aethernet, list) in data.AethernetToNodeDistances)
        {
            var to = list.FirstOrDefault(x => x.Id == toId);
            if (to.Id != toId)
            {
                continue;
            }

            var cost = returnCost + teleportCost + to.Distance;
            if (cost < bestCost)
            {
                bestCost = cost;
                if (aethernet == Aethernet.BaseCamp)
                {
                    bestSteps =
                    [
                        PathfinderStep.ReturnToBaseCamp(),
                        PathfinderStep.WalkToDestination(toId),
                    ];
                }
                else
                {
                    bestSteps =
                    [
                        PathfinderStep.ReturnToBaseCamp(),
                        PathfinderStep.TeleportToAethernet(aethernet),
                        PathfinderStep.WalkToDestination(toId),
                    ];
                }
            }
        }

        return (bestCost, bestSteps);
    }

    /// <summary>開始讀成本表、不等它讀完：讀好是 FileLoaded；找不到檔、讀檔或解析失敗是 LoadFailed（已寫 Error）。不擲出。</summary>
    /// <remarks>刻意不用 async void：那裡的例外不會交給 Dalamud，而是在執行緒池上成為未處理例外、整個遊戲結束。</remarks>
    protected void LoadFile(string filename)
    {
        State = PathfinderState.LoadingFile;
        string file;
        try
        {
            file = Path.Join(ZoneData.GetCurrentZoneDataDirectory(), filename);
        }
        catch (Exception ex)
        {
            Fail(ex, $"[BOCCHI][尋路] 讀不到成本表 {filename}：找不到目前區域的資料目錄（{ex.GetType().Name}：{ex.Message}）");
            return;
        }

        _ = LoadFromPath(file);
    }

    private async Task LoadFromPath(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                Fail(null, $"Required file not found: {file}");
                return;
            }

            var options = new JsonSerializerOptions
            {
                IncludeFields = false,
            };

            var json = await File.ReadAllTextAsync(file);
            var loaded = JsonSerializer.Deserialize<NodeDataSchema>(json, options);
            if (loaded.NodeToNodeDistances == null || loaded.AethernetToNodeDistances == null || loaded.NodeToAethernetDistances == null)
            {
                Fail(null, $"[BOCCHI][尋路] 成本表 {file} 缺少距離資料（JSON 裡有欄位是 null），不使用");
                return;
            }

            data = loaded;
            State = PathfinderState.FileLoaded;
        }
        catch (Exception ex)
        {
            Fail(ex, $"[BOCCHI][尋路] 讀不到成本表 {file}（{ex.GetType().Name}：{ex.Message}）");
        }
    }

    private void Fail(Exception? ex, string message)
    {
        State = PathfinderState.LoadFailed;
        Svc.Log.Error(ex, message);
    }

    protected Dictionary<uint, Dictionary<uint, (float Cost, List<PathfinderStep> Steps)>> BuildCostGraph(List<uint> nodes)
    {
        var graph = new Dictionary<uint, Dictionary<uint, (float, List<PathfinderStep>)>>();

        foreach (var from in nodes)
        {
            graph[from] = new Dictionary<uint, (float, List<PathfinderStep>)>();
            foreach (var to in nodes)
            {
                if (from == to)
                {
                    continue;
                }

                var result = GetBestSteps(from, to);
                graph[from][to] = result;
            }
        }

        return graph;
    }

    protected List<uint> SolveTSPNearestInsertion(
        uint start,
        List<uint> nodes,
        Dictionary<uint, Dictionary<uint, (float Cost, List<PathfinderStep> Steps)>> graph)
    {
        // 只有一個點時路線就是它本身；回 [start, start] 會讓 BuildStepPath 找不到 start→start 而寫一行 Error。
        if (nodes.Count == 1)
        {
            return [start];
        }


        var route = new List<uint> { start };
        var unvisited = new HashSet<uint>(nodes.Where(n => n != start));
        while (unvisited.Count > 0)
        {
            var bestInsertionCost = float.MaxValue;
            var bestIndex = -1;
            uint bestNode = 0;

            foreach (var nodeToInsert in unvisited)
            {
                for (var i = 0; i < route.Count; i++)
                {
                    var from = route[i];
                    uint to;

                    if (i == route.Count - 1)
                    {
                        var costAtEnd = graph[from][nodeToInsert].Cost;
                        if (costAtEnd < bestInsertionCost)
                        {
                            bestInsertionCost = costAtEnd;
                            bestIndex = route.Count;
                            bestNode = nodeToInsert;
                        }
                    }
                    else
                    {
                        to = route[i + 1];

                        var addedCost = graph[from][nodeToInsert].Cost + graph[nodeToInsert][to].Cost
                                        - graph[from][to].Cost;

                        if (addedCost < bestInsertionCost)
                        {
                            bestInsertionCost = addedCost;
                            bestIndex = i + 1;
                            bestNode = nodeToInsert;
                        }
                    }
                }
            }

            if (bestIndex != -1)
            {
                route.Insert(bestIndex, bestNode);
                unvisited.Remove(bestNode);
            }
            else
            {
                Svc.Log.Error($"Could not find best insertion point for {unvisited.First()}");
                break;
            }
        }

        return route;
    }

    protected List<PathfinderStep> BuildStepPath(List<uint> orderedNodes, Dictionary<uint, Dictionary<uint, (float Cost, List<PathfinderStep> Steps)>> graph)
    {
        List<PathfinderStep> steps = [];

        for (var i = 0; i < orderedNodes.Count - 1; i++)
        {
            var from = orderedNodes[i];
            var to = orderedNodes[i + 1];
            if (graph.ContainsKey(from) && graph[from].ContainsKey(to))
            {
                steps.AddRange(graph[from][to].Steps);
            }
            else
            {
                Svc.Log.Error($"Graph missing path from {from} to {to}. This should not happen if TSP is correct.");
            }
        }

        return steps;
    }

    protected void PrintPath(List<PathfinderStep> steps)
    {
        // 逐步清單只給除錯用；規劃了幾點看結尾的 Total 行與巡迴結束的 [寶箱巡迴] 行。
        Svc.Log.Debug("== Pathfinder Steps ==");

        var index = 1;
        var treasureCount = 0;

        foreach (var step in steps)
        {
            var message = step.Type switch
            {
                PathfinderStepType.WalkToNode => $"[{index}] Walk to Destination: {step.NodeId}",
                PathfinderStepType.WalkToAethernet => $"[{index}] Walk to Aethernet: {step.Aethernet}",
                PathfinderStepType.TeleportToAethernet => $"[{index}] Teleport to Aethernet: {step.Aethernet}",
                PathfinderStepType.ReturnToBaseCamp => $"[{index}] Return to Base Camp",
                _ => $"[{index}] Unknown Step Type",
            };

            if (step.Type == PathfinderStepType.WalkToNode)
            {
                treasureCount++;
            }

            Svc.Log.Debug(message);
            index++;
        }

        Svc.Log.Info($"== Total treasures visited: {treasureCount} ==");
        Svc.Log.Debug("=======================");
    }
}
