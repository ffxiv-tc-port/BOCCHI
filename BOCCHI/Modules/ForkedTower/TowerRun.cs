using System.Collections.Generic;
using System.Linq;
using BOCCHI.Data.Traps;
using BOCCHI.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Colors;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using Ocelot.Modules;
using Ocelot.Windows;

namespace BOCCHI.Modules.ForkedTower;

public class TowerRun(string hash)
{
    public readonly string Hash = hash;

    // ⚠️ 只拿來當「這個陷阱發現過了沒」的去重鍵，值從不被讀回；
    // 存 IEventObj 包裝等於跨幀持有原生指標（見 TrackedGroup.Traps 的說明），
    // 所以改存 GameObjectId 純量。
    private readonly Dictionary<string, ulong> DiscoveredTraps = [];

    private readonly Dictionary<string, TrackedGroup> TrackedGroups = [];

    public bool HasDiscoveredAllTraps(TrapGroup group)
    {
        if (TrackedGroups.TryGetValue(group.GetKey(), out var trackedGroup))
        {
            return trackedGroup.HasDiscoveredAllTraps();
        }

        return false;
    }

    public void Update(UpdateContext context)
    {
        foreach (var trap in GetNearbyTraps())
        {
            if (!DiscoveredTraps.TryAdd(trap.GetKey(), trap.GameObjectId))
            {
                continue;
            }

            var group = TrapData.GetGroup(trap);

            if (!TrackedGroups.TryGetValue(group.GetKey(), out var trackedGroup))
            {
                trackedGroup = new TrackedGroup(group);
                TrackedGroups.Add(group.GetKey(), trackedGroup);
            }

            trackedGroup.Traps.Add(trap.GameObjectId);
        }
    }

    public void Render(RenderContext context)
    {
        if (context.Config is not Config config)
        {
            return;
        }

        foreach (var trap in GetNearbyTraps())
        {
            if (Player.DistanceTo(trap) > config.ForkedTowerConfig.TrapDrawRange)
            {
                continue;
            }

            if (config.ForkedTowerConfig.DrawSmallTrapRange && trap.BaseId == (uint)OccultObjectType.Trap)
            {
                context.DrawCircle(trap.Position, 7f, ImGuiColors.DPSRed);
            }

            if (config.ForkedTowerConfig.DrawBigTrapRange && trap.BaseId == (uint)OccultObjectType.BigTrap)
            {
                context.DrawCircle(trap.Position, 30f, ImGuiColors.DalamudOrange);
            }
        }
    }

    private IEnumerable<IEventObj> GetNearbyTraps()
    {
        return Svc.Objects.OfType<IEventObj>().Where(o => o.BaseId is (uint)OccultObjectType.Trap or (uint)OccultObjectType.BigTrap);
    }
}
