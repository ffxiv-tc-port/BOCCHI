using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using BOCCHI.Enums;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace BOCCHI.Data;

public static class ZoneData
{
    public const uint SOUTHHORN = 1252;

    // This can and should be filled using layout files or excel data
    public readonly static Dictionary<uint, Vector3> Aetherytes = new()
    {
        { SOUTHHORN, new Vector3(830.75f, 72.98f, -695.98f) },
    };

    public readonly static Dictionary<uint, Vector3> StartingLocations = new()
    {
        { SOUTHHORN, new Vector3(850.33f, 72.99f, -704.07f) },
    };

    // Zone functions
    private static bool IsInSouthHorn()
    {
        return Svc.ClientState.TerritoryType == SOUTHHORN;
    }

    public static bool IsInOccultCrescent()
    {
        return Svc.Objects.LocalPlayer != null && IsInSouthHorn();
    }

    // Tower functions
    private static bool IsInForkedTowerBlood()
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        return HasForkedTowerStatus(player) && IsInSouthHorn();
    }

    // 同 player.StatusList.HasAny(三種狀態)：條件照 Dalamud StatusList 的走訪（PlayerState 已載入、只看有效的狀態格），
    // 但直接讀狀態陣列、不建 StatusList／Status 包裝：主視窗與世界疊加層每幀都問。框架執行緒專用。
    private static unsafe bool HasForkedTowerStatus(IGameObject player)
    {
        if (player.Address == nint.Zero || !Svc.PlayerState.IsLoaded)
        {
            return false;
        }

        ref var manager = ref ((BattleChara*)player.Address)->StatusManager;
        var statuses = manager.Status;
        var count = Math.Min(manager.NumValidStatuses, statuses.Length);
        for (var i = 0; i < count; i++)
        {
            if (statuses[i].StatusId is (ushort)PlayerStatus.DutiesAsAssigned
                or (ushort)PlayerStatus.ResurrectionDenied
                or (ushort)PlayerStatus.ResurrectionRestricted)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsInForkedTower()
    {
        return IsInForkedTowerBlood();
    }

    /// <summary>判準同 IsInForkedTower，但直接讀玩家的狀態陣列、不配置：給每幀路徑用。框架執行緒專用。</summary>
    public static unsafe bool IsInForkedTowerNoAlloc(IGameObject player)
    {
        if (!IsInSouthHorn() || player.Address == nint.Zero)
        {
            return false;
        }

        var statuses = ((BattleChara*)player.Address)->StatusManager.Status;
        for (var i = 0; i < statuses.Length; i++)
        {
            if (statuses[i].StatusId is (ushort)PlayerStatus.DutiesAsAssigned
                or (ushort)PlayerStatus.ResurrectionDenied
                or (ushort)PlayerStatus.ResurrectionRestricted)
            {
                return true;
            }
        }

        return false;
    }

    private static string GetCurrentZoneName()
    {
        if (IsInSouthHorn())
        {
            return "South Horn";
        }

        throw new Exception("Unknown Zone");
    }

    public static string GetCurrentZoneDataDirectory()
    {
        var directory = Path.Join(Svc.PluginInterface.AssemblyLocation.DirectoryName, "Data", GetCurrentZoneName().Replace(" ", ""));
        Directory.CreateDirectory(directory);

        return directory;
    }

    public static Aethernet GetClosestAethernetShard(Vector3 position)
    {
        return AethernetData.GetClosestTo(position).Aethernet;
    }

    public static IList<IGameObject> GetNearbyAethernetShards(float range = 4.3f)
    {
        var playerPos = Svc.Objects.LocalPlayer?.Position ?? Vector3.Zero;

        return Svc.Objects
            .Where(o => o.ObjectKind == ObjectKind.EventObj)
            .Where(o => AethernetData.All().Select((datum) => datum.DataId).Contains(o.BaseId))
            .Where(o => Vector3.Distance(o.Position, playerPos) <= range)
            .ToList();
    }

    /// <summary>同 GetNearbyAethernetShards(range).Count > 0，不建清單：主視窗每一列每幀都問。</summary>
    public static bool IsNearAnyAethernetShard(float range = 4.3f)
    {
        var playerPos = Svc.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        foreach (var o in Svc.Objects)
        {
            if (o.ObjectKind == ObjectKind.EventObj && AethernetData.IsShard(o.BaseId) && Vector3.Distance(o.Position, playerPos) <= range)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>附近（range 內）有沒有 aethernet 這一顆；條件同 GetNearbyAethernetShards 再比 DataId，不建清單。</summary>
    public static bool IsNearAethernetShard(Aethernet aethernet, float range = 4.3f)
    {
        var dataId = AethernetData.DataIdOf(aethernet);
        var playerPos = Svc.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        foreach (var o in Svc.Objects)
        {
            if (o.ObjectKind == ObjectKind.EventObj && o.BaseId == dataId && Vector3.Distance(o.Position, playerPos) <= range)
            {
                return true;
            }
        }

        return false;
    }

    public static IList<IGameObject> GetNearbyKnowledgeCrystal(float range = 4.5f)
    {
        var playerPos = Svc.Objects.LocalPlayer?.Position ?? Vector3.Zero;

        return Svc.Objects
            .Where(o => o.ObjectKind == ObjectKind.EventObj)
            .Where(o => o.BaseId == (uint)OccultObjectType.KnowledgeCrystal)
            .Where(o => Vector3.Distance(o.Position, playerPos) <= range)
            .ToList();
    }

    /// <summary>同 GetNearbyKnowledgeCrystal(range).Count > 0，不建清單：主視窗每幀都問。</summary>
    public static bool IsNearKnowledgeCrystal(float range = 4.5f)
    {
        var playerPos = Svc.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        foreach (var o in Svc.Objects)
        {
            if (o.ObjectKind == ObjectKind.EventObj && o.BaseId == (uint)OccultObjectType.KnowledgeCrystal && Vector3.Distance(o.Position, playerPos) <= range)
            {
                return true;
            }
        }

        return false;
    }
}
