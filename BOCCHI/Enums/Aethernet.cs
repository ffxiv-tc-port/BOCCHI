using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BOCCHI.Data;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using Lumina.Excel.Sheets;

namespace BOCCHI.Enums;

public enum Aethernet : uint
{
    BaseCamp = 4944,
    TheWanderersHaven = 4936,
    CrystallizedCaverns = 4929,
    Eldergrowth = 4930,
    Stonemarsh = 4942,
}

public class AethernetData
{
    public readonly static float DISTANCE = 3.8f;

    public Aethernet Aethernet;

    public uint DataId;

    public Vector3 Position;


    public Vector3 Destination; // Where you end up after teleporting to this shard

    public static IEnumerable<AethernetData> All()
    {
        return ((Aethernet[])Enum.GetValues(typeof(Aethernet)))
            .Select(a => a.GetData());
    }

    public static IOrderedEnumerable<AethernetData> AllByDistance()
    {
        return AllByDistance(Player.Position);
    }

    public static IOrderedEnumerable<AethernetData> AllByDistance(Vector3 position)
    {
        return All().OrderBy(a => Vector3.Distance(a.Position, position));
    }

    // GetClosestTo 每幀會被呼叫（非法模式待命時）：只建一次。順序同 All()，平手取前面的（同 OrderBy().First()）。
    private static AethernetData[]? closestCandidates;

    /// <summary>同 aethernet.GetData().DataId，不新建物件。</summary>
    public static uint DataIdOf(Aethernet aethernet)
    {
        var all = closestCandidates ??= All().ToArray();
        for (var i = 0; i < all.Length; i++)
        {
            if (all[i].Aethernet == aethernet)
            {
                return all[i].DataId;
            }
        }

        return aethernet.GetData().DataId;
    }

    /// <summary>dataId 是不是某一顆傳送水晶（同 All().Select(d => d.DataId).Contains(dataId)）。</summary>
    public static bool IsShard(uint dataId)
    {
        var all = closestCandidates ??= All().ToArray();
        for (var i = 0; i < all.Length; i++)
        {
            if (all[i].DataId == dataId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>回傳的是共用的實例，不要改它的欄位。</summary>
    public static AethernetData GetClosestTo(Vector3 to)
    {
        var all = closestCandidates ??= All().ToArray();
        var best = all[0];
        var bestDistance = Vector3.Distance(to, best.Position);
        for (var i = 1; i < all.Length; i++)
        {
            var distance = Vector3.Distance(to, all[i].Position);
            if (distance < bestDistance)
            {
                best = all[i];
                bestDistance = distance;
            }
        }

        return best;
    }

    public static AethernetData GetClosestToPlayer()
    {
        return GetClosestTo(Player.Position);
    }

    public float DistanceTo(Vector3 to)
    {
        return Vector3.Distance(to, Position);
    }

    public float DistanceToPlayer()
    {
        return DistanceTo(Player.Position);
    }
}

public static class AethernetExtensions
{
    public static string ToFriendlyString(this Aethernet aethernet)
    {
        // O(1) 查表；查不到時回傳空字串，而不是讓 default(PlaceName).Name 丟 NullReferenceException
        return Svc.Data.GetExcelSheet<PlaceName>().GetRowOrDefault((uint)aethernet)?.Name.ToString() ?? string.Empty;
    }

    public static AethernetData GetData(this Aethernet aethernet)
    {
        switch (aethernet)
        {
            case Aethernet.BaseCamp:
                return new AethernetData
                {
                    Aethernet = Aethernet.BaseCamp,
                    DataId = 2014664,
                    Position = ZoneData.Aetherytes[ZoneData.SOUTHHORN],
                    Destination = new Vector3(835.3f, 73f, -695.9f),
                };
            case Aethernet.TheWanderersHaven:
                return new AethernetData
                {
                    Aethernet = Aethernet.TheWanderersHaven,
                    DataId = 2014665,
                    Position = new Vector3(-173.02f, 8.19f, -611.14f),
                    Destination = new Vector3(-169.1f, 6.5f, -609.4f),
                };
            case Aethernet.CrystallizedCaverns:
                return new AethernetData
                {
                    Aethernet = Aethernet.CrystallizedCaverns,
                    DataId = 2014666,
                    Position = new Vector3(-358.14f, 101.98f, -120.96f),
                    Destination = new Vector3(-354.6f, 100f, -120.7f),
                };
            case Aethernet.Eldergrowth:
                return new AethernetData
                {
                    Aethernet = Aethernet.Eldergrowth,
                    DataId = 2014667,
                    Position = new Vector3(306.94f, 105.18f, 305.65f),
                    Destination = new Vector3(302.3f, 103f, 306f),
                };
            case Aethernet.Stonemarsh:
                return new AethernetData
                {
                    Aethernet = Aethernet.Stonemarsh,
                    DataId = 2014744,
                    Position = new Vector3(-384.12f, 99.20f, 281.42f),
                    Destination = new Vector3(-384f, 97.2f, 278.1f),
                };
            default:
                return Aethernet.BaseCamp.GetData();
        }
    }
}
