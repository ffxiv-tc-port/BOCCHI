using System.Collections.Generic;
using System.Numerics;

namespace BOCCHI.Modules.Treasure;

/// <summary>
/// 記住處理過的寶箱。同一個＝物件 id 相同、而且位置相差不到 <see cref="SamePositionTolerance"/> 米：
/// id 不同就是另一個寶箱（重生會換 id），id 相同但位置不同也當成另一個（防 id 被重用）。
/// 只存純量、不存物件包裝；不加鎖，框架執行緒專用。
/// </summary>
public sealed class SilverChestMemory(int capacity)
{
    public const float SamePositionTolerance = 1f;

    private readonly List<(ulong Id, Vector3 Position)> entries = [];

    public int Count
    {
        get => entries.Count;
    }

    public bool Contains(ulong id, Vector3 position)
    {
        foreach (var entry in entries)
        {
            if (entry.Id == id && Vector3.DistanceSquared(entry.Position, position) <= SamePositionTolerance * SamePositionTolerance)
            {
                return true;
            }
        }

        return false;
    }

    /// <returns>true＝第一次見到，已記下；滿了先丟最舊的一筆。</returns>
    public bool Add(ulong id, Vector3 position)
    {
        if (Contains(id, position))
        {
            return false;
        }

        if (capacity > 0 && entries.Count >= capacity)
        {
            entries.RemoveAt(0);
        }

        entries.Add((id, position));
        return true;
    }

    public void Clear()
    {
        entries.Clear();
    }
}
