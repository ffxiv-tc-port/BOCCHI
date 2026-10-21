using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using BOCCHI.Enums;
using Ocelot.IPC;

namespace BOCCHI.Pathfinding;

/// <summary>
/// 規劃第一段之前，向 vnavmesh 量角色目前位置到候選點與最近幾個乙太之晶的步行路長（Nav.Pathfind：只算路、不移動）。
/// 只在 framework 執行緒建立、輪詢與 <see cref="Build"/>；量不到（失敗、逾時、網格沒好、超過查詢上限）的改用直線距離。
/// 到時限或 <see cref="Build"/> 時取消還沒算完的查詢（只取消自己的，不用全域的 Nav.PathfindCancelAll），不佔住 vnavmesh 的尋路佇列。
/// </summary>
public sealed class StartLegSurvey
{
    public const int MaxNodeQueries = 16;

    public const int ShardQueries = 2;

    public const long TimeLimitMs = 3000;

    /// <summary>路徑終點離生成點超過這麼遠＝走不到（巡迴要走到點的 5 米內才認得點上的物件）。</summary>
    public const float NodeReachM = 5f;

    private readonly record struct Query(Vector3 Target, float Reach, Task<List<Vector3>>? Task);

    private readonly List<(uint Node, Query Query)> nodes = [];

    private readonly List<(Aethernet Shard, Query Query)> shards = [];

    private readonly long deadline;

    private readonly CancellationTokenSource cancel = new();

    private StartLegSurvey(Vector3 start, long deadline)
    {
        Start = start;
        this.deadline = deadline;
    }

    public Vector3 Start { get; }

    /// <summary>沒有送出任何查詢的原因；null＝有送。</summary>
    public string? NotSent { get; private set; }

    public static StartLegSurvey Begin(VNavmesh vnav, Vector3 start, IReadOnlyList<(uint Node, Vector3 Position)> candidates, long now)
    {
        var survey = new StartLegSurvey(start, now + TimeLimitMs);
        bool ready;
        try
        {
            ready = vnav.IsNavmeshReady();
            survey.NotSent = ready ? null : "導航網格還沒好";
        }
        catch (Exception ex)
        {
            ready = false;
            survey.NotSent = $"查詢 vnavmesh 失敗（{ex.GetType().Name}）";
        }

        var ordered = candidates.OrderBy(c => Vector3.Distance(start, c.Position)).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var (node, position) = ordered[i];
            survey.nodes.Add((node, new Query(position, NodeReachM, ready && i < MaxNodeQueries ? TryPathfind(vnav, start, position, survey.cancel.Token) : null)));
        }

        foreach (var shard in AethernetData.AllByDistance(start).Take(ShardQueries))
        {
            survey.shards.Add((shard.Aethernet, new Query(shard.Position, AethernetData.DISTANCE, ready ? TryPathfind(vnav, start, shard.Position, survey.cancel.Token) : null)));
        }

        return survey;
    }

    private static Task<List<Vector3>>? TryPathfind(VNavmesh vnav, Vector3 from, Vector3 to, CancellationToken token)
    {
        try
        {
            Task<List<Vector3>>? task;
            try
            {
                task = vnav.PathfindCancelable(from, to, false, token);
            }
            catch (Exception)
            {
                // 拿不到可取消的端點時照舊用不能取消的那支。
                task = vnav.Pathfind(from, to, false);
            }

            // 逾時後才失敗的查詢沒有人等：先掛上觀察，例外不會變成未觀察的例外。
            task?.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            return task;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool IsDone(long now)
    {
        if (now >= deadline)
        {
            CancelPending();
            return true;
        }

        foreach (var (_, query) in nodes)
        {
            if (query.Task is { IsCompleted: false })
            {
                return false;
            }
        }

        foreach (var (_, query) in shards)
        {
            if (query.Task is { IsCompleted: false })
            {
                return false;
            }
        }

        return true;
    }

    /// <param name="answered">vnavmesh 有回答（量到或走不到）的筆數；0＝全部是直線。</param>
    public StartLegCosts Build(out int answered)
    {
        CancelPending();
        var costs = new StartLegCosts();
        answered = 0;
        foreach (var (node, query) in nodes)
        {
            var walk = FromPath(Start, query.Target, query.Reach, Completed(query.Task));
            answered += walk.Source == StartWalkSource.Straight ? 0 : 1;
            costs.Nodes[node] = walk;
        }

        foreach (var (shard, query) in shards)
        {
            var walk = FromPath(Start, query.Target, query.Reach, Completed(query.Task));
            answered += walk.Source == StartWalkSource.Straight ? 0 : 1;
            costs.Shards[shard] = walk;
        }

        return costs;
    }

    // 不 Dispose：vnavmesh 的連結取消來源還掛著它的註冊，交給 GC。
    private void CancelPending()
    {
        if (!cancel.IsCancellationRequested)
        {
            cancel.Cancel();
        }
    }

    private static List<Vector3>? Completed(Task<List<Vector3>>? task)
    {
        return task is { IsCompletedSuccessfully: true } ? task.Result : null;
    }

    /// <summary>純函式：null 或空路徑＝量不到（直線）；終點離目標超過 <paramref name="reach"/>＝走不到；路長從 <paramref name="start"/> 算起。</summary>
    public static StartWalk FromPath(Vector3 start, Vector3 target, float reach, List<Vector3>? path)
    {
        var straight = Vector3.Distance(start, target);
        if (path == null || path.Count == 0)
        {
            return new StartWalk(straight, StartWalkSource.Straight, straight);
        }

        var length = Vector3.Distance(start, path[0]);
        for (var i = 1; i < path.Count; i++)
        {
            length += Vector3.Distance(path[i - 1], path[i]);
        }

        return Vector3.Distance(path[^1], target) > reach
            ? new StartWalk(length, StartWalkSource.Unreachable, straight)
            : new StartWalk(length, StartWalkSource.Measured, straight);
    }
}
