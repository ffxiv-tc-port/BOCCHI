using System;
using System.Collections.Generic;
using System.Numerics;
using BOCCHI.Enums;

namespace BOCCHI.Modules.Treasure;

/// <summary>寶箱生成點（區域版面裡的 Treasure 列）：Id＝Treasure 表列號，種類跟著模型固定不變。</summary>
public readonly record struct TreasureSpawnPoint(uint Id, Vector3 Position, TreasureType Type);

/// <summary>這一幀物件表裡看得到的寶箱。Opened＝已經被開過、還沒從物件表消失。</summary>
public readonly record struct SeenTreasure(Vector3 Position, TreasureType Type, bool Opened);

/// <summary>記下來、還沒開的寶箱。Node＝對上的生成點；0＝對不上任何生成點（不能排進巡迴）。</summary>
public readonly record struct KnownTreasure(uint Node, Vector3 Position, TreasureType Type);

public enum TreasureMemoryChange
{
    ChestAdded = 1,

    /// <summary>看到寶箱已經開了。</summary>
    ChestOpened,

    /// <summary>走近了卻看不到：寶箱只有發現者自己開得到，多半是記錄沒看到的時候開掉了。</summary>
    ChestGone,

    NodeVisited,

    /// <summary>巡過的生成點又看到寶箱：以看到的為準，改回沒巡過。</summary>
    NodeReopened,
}

public readonly record struct TreasureMemoryEvent(TreasureMemoryChange Change, uint Node, Vector3 Position, TreasureType Type);

/// <summary>
/// 寶箱巡迴跨越單次巡迴的記錄：看到過、還沒開的寶箱，以及巡過（走近確認沒有寶箱、或寶箱已開）的生成點。
/// 依據的遊戲機制：寶箱只有發現者自己看得到、開得到，生成後不會自己消失。
/// 參與 F.A.T.E.／緊急遭遇戰不清任何記錄（只在參與期間不記巡過）；要重巡由使用者清除（<see cref="ClearRecords"/>）；
/// 換區、離島由呼叫端呼叫 <see cref="LeaveArea"/>（寶箱留著、其餘清），登出呼叫 <see cref="Reset"/> 全部清。
/// 純邏輯、不碰遊戲、沒有鎖，只在框架執行緒用；時間一律是呼叫端給的毫秒數，不配置（事件清單滿了才擴充）。
/// </summary>
public sealed class TreasureHuntMemory
{
    /// <summary>寶箱算在某個生成點上的最大距離；生成點彼此至少相隔約 80 米。</summary>
    public const float MatchRadius = 5f;

    /// <summary>剛開始看、或讀圖結束後這麼久內只記看到的寶箱，不判「沒有」：物件表還在補。</summary>
    public const long SettleMs = 2000;

    /// <summary>生成點要連續在偵測範圍內這麼久，才判那裡沒有寶箱。</summary>
    public const long DwellMs = 500;

    private const float SamePositionM = 1f;

    private const long NotInRange = long.MinValue;

    private TreasureSpawnPoint[] points = [];

    private long[] pointInRangeSince = [];

    private readonly HashSet<uint> visited = [];

    private readonly List<KnownTreasure> chests = [];

    private readonly List<long> chestInRangeSince = [];

    private readonly List<TreasureMemoryEvent> events = [];

    // 前往失敗（走不到）的已記錄寶箱所在生成點；清法同巡過（清除、換區、重設）。寶箱本身照樣記錄。
    private readonly HashSet<uint> unreachable = [];

    // long.MaxValue＝還沒開始看：第一次 Observe 才起算。
    private long settledAt = long.MaxValue;

    private int openedBronze;

    private int openedSilver;

    private bool hasParticipation;

    private ulong lastParticipation;

    /// <summary>正在參與 F.A.T.E.／緊急遭遇戰：這段期間不記巡過（參與結束後寶箱可能才重新出現）。</summary>
    public bool Participating { get; private set; }

    /// <summary>記錄有變就加一；給畫面判斷要不要重組文字。</summary>
    public int Version { get; private set; }

    public bool HasSpawnPoints
    {
        get => points.Length > 0;
    }

    public int SpawnPointCount
    {
        get => points.Length;
    }

    /// <summary>目前的生成點（還沒讀到是空的）；換生成點時換一個陣列，不要保留。</summary>
    public IReadOnlyList<TreasureSpawnPoint> SpawnPoints
    {
        get => points;
    }

    public int VisitedCount
    {
        get => visited.Count;
    }

    public int ChestCount
    {
        get => chests.Count;
    }

    public int UnreachableCount
    {
        get => unreachable.Count;
    }

    public IReadOnlyList<KnownTreasure> Chests
    {
        get => chests;
    }

    /// <summary>上次 <see cref="ClearEvents"/> 之後的逐項變動（給記錄用）。</summary>
    public IReadOnlyList<TreasureMemoryEvent> Events
    {
        get => events;
    }

    public void ClearEvents()
    {
        events.Clear();
    }

    /// <summary>記下的寶箱變成「開了」的累計次數（不分是誰開的；清除、重設都不歸零）：一段巡迴的開箱數＝前後相減。</summary>
    public int OpenedCount(TreasureType type)
    {
        return type switch
        {
            TreasureType.Bronze => openedBronze,
            TreasureType.Silver => openedSilver,
            _ => 0,
        };
    }

    public int CountChests(TreasureType type)
    {
        var count = 0;
        for (var i = 0; i < chests.Count; i++)
        {
            if (chests[i].Type == type)
            {
                count++;
            }
        }

        return count;
    }

    public bool IsVisited(uint node)
    {
        return visited.Contains(node);
    }

    public bool HasChestAt(uint node)
    {
        if (node == 0)
        {
            return false;
        }

        for (var i = 0; i < chests.Count; i++)
        {
            if (chests[i].Node == node)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>巡迴可以不走這個點：巡過了，而且那裡沒有已記錄的寶箱。</summary>
    public bool IsCovered(uint node)
    {
        return node != 0 && visited.Contains(node) && !HasChestAt(node);
    }

    /// <summary>這個生成點的寶箱前往失敗過：巡迴不排、不插隊，直到清除或重設。</summary>
    public bool IsUnreachable(uint node)
    {
        return node != 0 && unreachable.Contains(node);
    }

    /// <returns>true＝新記下（同一個點重複記不算）。</returns>
    public bool NoteUnreachable(uint node)
    {
        if (node == 0 || !unreachable.Add(node))
        {
            return false;
        }

        Version++;
        return true;
    }

    public bool TryGetSpawnPoint(uint node, out TreasureSpawnPoint point)
    {
        foreach (var candidate in points)
        {
            if (candidate.Id == node)
            {
                point = candidate;
                return true;
            }
        }

        point = default;
        return false;
    }

    /// <returns>最近、而且在 <see cref="MatchRadius"/> 內的生成點；沒有回 0。</returns>
    public uint MatchNode(Vector3 position)
    {
        uint best = 0;
        var bestDistance = MatchRadius * MatchRadius;
        foreach (var point in points)
        {
            var distance = Vector3.DistanceSquared(point.Position, position);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = point.Id;
            }
        }

        return best;
    }

    /// <summary>換上這個區域的生成點；之前對不上生成點的寶箱重新對一次。</summary>
    public void SetSpawnPoints(IReadOnlyList<TreasureSpawnPoint> spawnPoints)
    {
        points = new TreasureSpawnPoint[spawnPoints.Count];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = spawnPoints[i];
        }

        pointInRangeSince = new long[points.Length];
        Array.Fill(pointInRangeSince, NotInRange);
        for (var i = 0; i < chests.Count; i++)
        {
            var chest = chests[i];
            if (chest.Node != 0)
            {
                continue;
            }

            var node = MatchNode(chest.Position);
            if (node != 0)
            {
                chests[i] = chest with { Node = node };
                visited.Remove(node);
            }
        }

        Version++;
    }

    /// <summary>讀圖中（每幀呼叫）：結束後再等 <see cref="SettleMs"/> 才判「沒有」。</summary>
    public void NoteAreaTransition(long now)
    {
        settledAt = now + SettleMs;
        ResetDwell();
    }

    /// <summary>
    /// 每幀一次：先記看得到的寶箱，再對偵測範圍內的生成點與寶箱判「沒有」。
    /// <paramref name="range"/> 是巡迴的物件偵測範圍（3D 距離）；看到的寶箱不管多遠都記。
    /// </summary>
    public void Observe(long now, Vector3 player, float range, ReadOnlySpan<SeenTreasure> seen)
    {
        if (settledAt == long.MaxValue)
        {
            settledAt = now + SettleMs;
        }

        foreach (var chest in seen)
        {
            RecordSeen(chest);
        }

        if (now < settledAt)
        {
            ResetDwell();
            return;
        }

        var rangeSquared = range * range;
        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            if (!Absent(ref pointInRangeSince[i], now, player, rangeSquared, point.Position, seen))
            {
                continue;
            }

            RemoveChestAt(point.Id, point.Position, TreasureMemoryChange.ChestGone);
            MarkVisited(point.Id, point.Position, point.Type);
        }

        for (var i = chests.Count - 1; i >= 0; i--)
        {
            if (chests[i].Node != 0)
            {
                continue;
            }

            var since = chestInRangeSince[i];
            var absent = Absent(ref since, now, player, rangeSquared, chests[i].Position, seen);
            chestInRangeSince[i] = since;
            if (absent)
            {
                RemoveChestAtIndex(i, TreasureMemoryChange.ChestGone);
            }
        }
    }

    /// <summary>巡迴走到某個生成點、自己判斷那裡沒有寶箱（同一個偵測範圍）。讀圖剛結束時不採信。</summary>
    /// <returns>true＝記成巡過。</returns>
    public bool NoteAbsent(uint node, long now)
    {
        if (settledAt == long.MaxValue || now < settledAt || !TryGetSpawnPoint(node, out var point))
        {
            return false;
        }

        RemoveChestAt(node, point.Position, TreasureMemoryChange.ChestGone);
        return MarkVisited(node, point.Position, point.Type);
    }

    /// <summary>開箱步驟看到寶箱開了。可以重複呼叫。</summary>
    public void NoteOpened(Vector3 position)
    {
        var node = MatchNode(position);
        RemoveChestAt(node, position, TreasureMemoryChange.ChestOpened);
        if (node != 0 && TryGetSpawnPoint(node, out var point))
        {
            MarkVisited(node, point.Position, point.Type);
        }
    }

    /// <summary>開始參與一場 F.A.T.E.／緊急遭遇戰：參與期間不記巡過。刻意不清記錄：每打一場就重巡全部生成點太費時，要重巡由使用者清除。</summary>
    /// <returns>true＝新的一場；key 相同（同一場重進）回 false，key＝0（辨識不出是哪一場）一律算新的一場。</returns>
    public bool NoteParticipation(ulong key)
    {
        Participating = true;
        ResetDwell();
        if (key != 0 && hasParticipation && key == lastParticipation)
        {
            return false;
        }

        hasParticipation = true;
        lastParticipation = key;
        return true;
    }

    public void EndParticipation()
    {
        Participating = false;
        ResetDwell();
    }

    /// <summary>清掉寶箱、巡過與前往失敗（使用者按清除）；生成點與參與狀態不動。</summary>
    public (int Chests, int Visited) ClearRecords()
    {
        var result = (chests.Count, visited.Count);
        chests.Clear();
        chestInRangeSince.Clear();
        visited.Clear();
        unreachable.Clear();
        ResetDwell();
        Version++;
        return result;
    }

    /// <summary>登出：全部清，生成點下次重讀。</summary>
    public (int Chests, int Visited) Reset()
    {
        var result = ClearRecords();
        ForgetArea();
        return result;
    }

    /// <summary>
    /// 換區、離島、換副本實例：寶箱留著（只有自己開得到、不會自己消失），改回沒對上生成點，下次讀到生成點再對；
    /// 巡過、前往失敗、生成點與參與狀態照 <see cref="Reset"/> 清。
    /// </summary>
    /// <returns>留下的寶箱數、清掉的巡過數。</returns>
    public (int Chests, int Visited) LeaveArea()
    {
        var result = (chests.Count, visited.Count);
        for (var i = 0; i < chests.Count; i++)
        {
            chests[i] = chests[i] with { Node = 0 };
        }

        visited.Clear();
        unreachable.Clear();
        ForgetArea();
        Version++;
        return result;
    }

    private void ForgetArea()
    {
        points = [];
        pointInRangeSince = [];
        settledAt = long.MaxValue;
        Participating = false;
        hasParticipation = false;
        lastParticipation = 0;
        events.Clear();
        ResetDwell();
    }

    private void RecordSeen(SeenTreasure seen)
    {
        if (seen.Type is not (TreasureType.Bronze or TreasureType.Silver))
        {
            return;
        }

        var node = MatchNode(seen.Position);
        if (seen.Opened)
        {
            RemoveChestAt(node, seen.Position, TreasureMemoryChange.ChestOpened);
            if (node != 0)
            {
                MarkVisited(node, seen.Position, seen.Type);
            }

            return;
        }

        var index = FindChest(node, seen.Position);
        if (index < 0)
        {
            chests.Add(new KnownTreasure(node, seen.Position, seen.Type));
            chestInRangeSince.Add(NotInRange);
            events.Add(new TreasureMemoryEvent(TreasureMemoryChange.ChestAdded, node, seen.Position, seen.Type));
            Version++;
        }
        else if (chests[index].Type != seen.Type || Vector3.DistanceSquared(chests[index].Position, seen.Position) > SamePositionM * SamePositionM)
        {
            chests[index] = new KnownTreasure(node, seen.Position, seen.Type);
            Version++;
        }

        if (node != 0 && visited.Remove(node))
        {
            events.Add(new TreasureMemoryEvent(TreasureMemoryChange.NodeReopened, node, seen.Position, seen.Type));
            Version++;
        }
    }

    // 在範圍內連續 DwellMs 而且附近看不到任何寶箱（含已開的：那由 RecordSeen 處理）。
    private static bool Absent(ref long since, long now, Vector3 player, float rangeSquared, Vector3 position, ReadOnlySpan<SeenTreasure> seen)
    {
        if (Vector3.DistanceSquared(player, position) > rangeSquared)
        {
            since = NotInRange;
            return false;
        }

        if (since == NotInRange)
        {
            since = now;
        }

        if (now - since < DwellMs)
        {
            return false;
        }

        foreach (var chest in seen)
        {
            if (Vector3.DistanceSquared(chest.Position, position) <= MatchRadius * MatchRadius)
            {
                return false;
            }
        }

        return true;
    }

    private int FindChest(uint node, Vector3 position)
    {
        for (var i = 0; i < chests.Count; i++)
        {
            var chest = chests[i];
            if (node != 0 ? chest.Node == node : chest.Node == 0 && Vector3.DistanceSquared(chest.Position, position) <= MatchRadius * MatchRadius)
            {
                return i;
            }
        }

        return -1;
    }

    private void RemoveChestAt(uint node, Vector3 position, TreasureMemoryChange why)
    {
        var index = FindChest(node, position);
        if (index >= 0)
        {
            RemoveChestAtIndex(index, why);
        }
    }

    private void RemoveChestAtIndex(int index, TreasureMemoryChange why)
    {
        var chest = chests[index];
        chests.RemoveAt(index);
        chestInRangeSince.RemoveAt(index);
        events.Add(new TreasureMemoryEvent(why, chest.Node, chest.Position, chest.Type));
        Version++;
        if (why != TreasureMemoryChange.ChestOpened)
        {
            return;
        }

        if (chest.Type == TreasureType.Bronze)
        {
            openedBronze++;
        }
        else if (chest.Type == TreasureType.Silver)
        {
            openedSilver++;
        }
    }

    private bool MarkVisited(uint node, Vector3 position, TreasureType type)
    {
        if (Participating || !visited.Add(node))
        {
            return false;
        }

        events.Add(new TreasureMemoryEvent(TreasureMemoryChange.NodeVisited, node, position, type));
        Version++;
        return true;
    }

    private void ResetDwell()
    {
        Array.Fill(pointInRangeSince, NotInRange);
        for (var i = 0; i < chestInRangeSince.Count; i++)
        {
            chestInRangeSince[i] = NotInRange;
        }
    }
}
