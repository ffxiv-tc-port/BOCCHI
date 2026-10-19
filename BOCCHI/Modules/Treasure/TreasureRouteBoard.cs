using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.Pathfinder;
using BOCCHI.Pathfinding;
using ECommons.DalamudServices;

namespace BOCCHI.Modules.Treasure;

/// <summary>
/// 主視窗「路線」選單的各段數字（依 <see cref="TreasureRoutes.Order"/>）。只在框架執行緒用：<see cref="Update"/> 每秒最多重算一次、
/// 輸入沒變不算；成本表在背景讀一次、只留距離，讀好之前時間是 ?。
/// </summary>
public sealed class TreasureRouteBoard
{
    public const long RefreshMs = 1000;

    private const string CostFile = "precomputed_treasure_hunt_data.json";

    private readonly TreasureRouteRow[] rows = new TreasureRouteRow[TreasureRoutes.Order.Length];

    private readonly string[] names = new string[TreasureRoutes.Order.Length];

    // 剩下的點沒變就不重估剩下的時間（巡迴中記錄常常變，但同一段的點很少變）。
    private readonly List<uint>?[] lastRemaining = new List<uint>?[TreasureRoutes.Order.Length];

    private bool namesRead;

    private Task<NodeDataSchema>? loading;

    private NodeDataSchema? costs;

    private Estimator? estimator;

    private long nextRefreshAt;

    private (bool Computed, int MaxLevel, int Memory, int Selection, bool Spawn, bool Costs, float Return, float Teleport) inputs;

    public TreasureRouteBoard()
    {
        Array.Fill(rows, TreasureRouteRow.Unknown);
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = TreasureRoutes.Order[i].ToString();
        }
    }

    /// <summary>數字或名稱變了就加一；給畫面判斷要不要重組文字。</summary>
    public int Version { get; private set; }

    public IReadOnlyList<TreasureRouteRow> Rows
    {
        get => rows;
    }

    /// <summary>算的時候用的區域等級上限；0＝還沒算過。</summary>
    public int MaxLevel { get; private set; }

    /// <summary>不在所選路線上的已記錄寶箱數（見 <see cref="TreasureRouteStats.CountOtherRouteChests"/>）。</summary>
    public int OtherRouteChests { get; private set; }

    /// <summary>成本表讀不到（時間一直是 ?）。</summary>
    public bool CostsUnavailable { get; private set; }

    public string Name(int index)
    {
        return names[index];
    }

    /// <param name="selection">所選路線的位元（第 i 位＝<see cref="TreasureRoutes.Order"/>[i]）。</param>
    public void Update(TreasureHuntMemory memory, PathfinderConfig config, int selection, long now)
    {
        if (now < nextRefreshAt)
        {
            return;
        }

        nextRefreshAt = now + RefreshMs;
        ReadNames();
        PollCosts();

        var knowledge = config.FollowKnowledgeLevel ? KnowledgeLevel.Read() : default;
        var maxLevel = PathfinderConfig.ChooseMaxLevel(config.FollowKnowledgeLevel, config.MaxLevel, knowledge).Level;
        var next = (Computed: true, MaxLevel: maxLevel, Memory: memory.Version, Selection: selection, Spawn: memory.HasSpawnPoints, Costs: costs.HasValue,
            Return: config.ReturnCost, Teleport: config.TeleportCost);
        if (next == inputs)
        {
            return;
        }

        var roundChanged = inputs.MaxLevel != maxLevel || inputs.Costs != next.Costs || inputs.Return != next.Return || inputs.Teleport != next.Teleport;
        inputs = next;
        if (costs is { } table && (estimator == null || estimator.ReturnCost != config.ReturnCost || estimator.TeleportCost != config.TeleportCost))
        {
            estimator = new Estimator(table, config.ReturnCost, config.TeleportCost);
        }

        for (var i = 0; i < rows.Length; i++)
        {
            var route = TreasureRoutes.Order[i];
            var nodes = TreasureRoutes.NodesOf(route, maxLevel);
            var row = rows[i];
            var round = roundChanged || row.Count < 0 ? (estimator == null ? -1f : TreasureRouteStats.EstimateSeconds(estimator, route, nodes)) : row.RoundSeconds;
            var left = memory.HasSpawnPoints ? TreasureRouteStats.RemainingOf(nodes, memory) : null;
            var rest = -1f;
            if (left != null && estimator != null)
            {
                rest = !roundChanged && row.RemainingSeconds >= 0 && lastRemaining[i] is { } last && last.SequenceEqual(left)
                    ? row.RemainingSeconds
                    : TreasureRouteStats.EstimateSeconds(estimator, route, left);
            }

            lastRemaining[i] = left;
            rows[i] = new TreasureRouteRow(nodes.Count, left?.Count ?? -1, round, rest);
        }

        MaxLevel = maxLevel;
        OtherRouteChests = TreasureRouteStats.CountOtherRouteChests(memory, maxLevel, route => IsSelected(selection, route));
        Version++;
    }

    /// <summary>下一次 <see cref="Update"/> 不等一秒（選擇剛改過）。</summary>
    public void RefreshSoon()
    {
        nextRefreshAt = 0;
    }

    /// <summary>每一段都選（＝全部巡）的位元。</summary>
    public static int All
    {
        get => (1 << TreasureRoutes.Order.Length) - 1;
    }

    public static bool IsSelected(int selection, Aethernet route)
    {
        var index = TreasureRoutes.IndexOf(route);
        return index >= 0 && (selection & (1 << index)) != 0;
    }

    /// <summary>不巡的路線 → 所選路線的位元；不認得的值略過。每幀呼叫：吃 List 才不會配置列舉器。</summary>
    public static int SelectionOf(List<Aethernet> excluded)
    {
        var selection = All;
        foreach (var route in excluded)
        {
            var index = TreasureRoutes.IndexOf(route);
            if (index >= 0)
            {
                selection &= ~(1 << index);
            }
        }

        return selection;
    }

    private void ReadNames()
    {
        if (namesRead)
        {
            return;
        }

        namesRead = true;
        for (var i = 0; i < names.Length; i++)
        {
            var name = TreasureRoutes.Order[i].ToFriendlyString();
            if (!string.IsNullOrEmpty(name))
            {
                names[i] = name;
            }
        }

        Version++;
    }

    // 路徑在框架執行緒取（要讀目前區域），讀檔與解析在背景；失敗只記一次，時間一直是 ?。
    private void PollCosts()
    {
        if (costs.HasValue || CostsUnavailable)
        {
            return;
        }

        if (loading == null)
        {
            var file = Path.Join(ZoneData.GetCurrentZoneDataDirectory(), CostFile);
            loading = Task.Run(() => LoadDistances(file));
            return;
        }

        if (!loading.IsCompleted)
        {
            return;
        }

        if (loading.IsCompletedSuccessfully)
        {
            costs = loading.Result;
        }
        else
        {
            CostsUnavailable = true;
            Svc.Log.Warning($"[BOCCHI][寶箱巡迴] 讀不到成本表 {CostFile}，路線選單的時間估計顯示 ?：{loading.Exception?.GetBaseException().Message}");
        }

        loading = null;
    }

    // 只留點 id 與距離：路徑座標占成本表大部分，估計用不到。
    private static NodeDataSchema LoadDistances(string file)
    {
        var schema = JsonSerializer.Deserialize<NodeDataSchema>(File.ReadAllText(file), new JsonSerializerOptions { IncludeFields = false });
        List<Position> none = [];
        foreach (var list in schema.NodeToNodeDistances.Values)
        {
            Strip(list, none);
        }

        foreach (var list in schema.AethernetToNodeDistances.Values)
        {
            Strip(list, none);
        }

        foreach (var list in schema.NodeToAethernetDistances.Values)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var entry = list[i];
                entry.Path = none;
                list[i] = entry;
            }
        }

        return schema;
    }

    private static void Strip(List<ToNode> list, List<Position> none)
    {
        for (var i = 0; i < list.Count; i++)
        {
            var entry = list[i];
            entry.Path = none;
            list[i] = entry;
        }
    }

    private sealed class Estimator(NodeDataSchema data, float returnCost, float teleportCost) : BasePathfinder(data, returnCost, teleportCost)
    {
        public float ReturnCost { get; } = returnCost;

        public float TeleportCost { get; } = teleportCost;

        protected override uint GetStartingNode(Vector3 start, List<uint> nodes)
        {
            return nodes[0];
        }
    }
}
