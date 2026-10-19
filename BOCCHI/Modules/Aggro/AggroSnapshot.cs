using System;
using System.Numerics;
using BOCCHI.Data;

namespace BOCCHI.Modules.Aggro;

/// <summary>一隻怪在本次掃描的快照；不保留任何原生指標或 IGameObject。</summary>
public struct AggroEntry
{
    public ulong GameObjectId;

    public uint NameId;

    public Vector3 Position;

    public float Rotation;

    public float HitboxRadius;

    /// <summary>物件上讀到的名牌等級；0＝讀不到。</summary>
    public int Level;

    public AggroSense Senses;

    public AggroState State;

    public bool BloodActive;

    public float DistanceSquared;
}

/// <summary>
/// 可重用的固定容量快照：掃描端（Update）寫、繪製端（Draw）讀，兩者都在遊戲主執行緒上依序執行。
/// 超過容量時保留離玩家最近的那幾筆。
/// </summary>
public sealed class AggroSnapshot
{
    public const int Capacity = 64;

    private readonly AggroEntry[] entries = new AggroEntry[Capacity];

    public int Count { get; private set; }

    /// <summary>最後一次完成掃描的 Environment.TickCount64；0＝還沒掃過。</summary>
    public long ScannedAt { get; private set; }

    public int CertainCount { get; private set; }

    public int PossibleCount { get; private set; }

    /// <summary>範圍內但判定為不畫的怪（等級低於知見等級、刻意排除）。</summary>
    public int SkippedCount { get; private set; }

    /// <summary>範圍內可畫但因為容量滿了被擠掉的怪。</summary>
    public int DroppedCount { get; private set; }

    public ReadOnlySpan<AggroEntry> Entries
    {
        get => entries.AsSpan(0, Count);
    }

    public void Begin()
    {
        Count = 0;
        CertainCount = 0;
        PossibleCount = 0;
        SkippedCount = 0;
        DroppedCount = 0;
    }

    public void Complete(long now)
    {
        ScannedAt = now;
    }

    public void Clear()
    {
        Begin();
        ScannedAt = 0;
    }

    public void CountSkipped()
    {
        SkippedCount++;
    }

    public void Offer(in AggroEntry entry)
    {
        if (entry.State == AggroState.None)
        {
            SkippedCount++;
            return;
        }

        if (Count < Capacity)
        {
            entries[Count++] = entry;
            Tally(entry.State, 1);
            return;
        }

        var farthest = 0;
        for (var i = 1; i < Count; i++)
        {
            if (entries[i].DistanceSquared > entries[farthest].DistanceSquared)
            {
                farthest = i;
            }
        }

        DroppedCount++;
        if (entry.DistanceSquared >= entries[farthest].DistanceSquared)
        {
            return;
        }

        Tally(entries[farthest].State, -1);
        entries[farthest] = entry;
        Tally(entry.State, 1);
    }

    /// <summary>快照是否夠新可以畫：掃描停了（離開區域、過場、Update 被略過）就不畫舊位置。</summary>
    public bool IsFresh(long now, long maxAgeMs)
    {
        return ScannedAt != 0 && now - ScannedAt <= maxAgeMs;
    }

    private void Tally(AggroState state, int delta)
    {
        if (state == AggroState.Certain)
        {
            CertainCount += delta;
        }
        else
        {
            PossibleCount += delta;
        }
    }
}
