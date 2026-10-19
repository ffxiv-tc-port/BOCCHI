using System.Collections.Generic;
using BOCCHI.Data.Traps;

namespace BOCCHI.Modules.ForkedTower;

public class TrackedGroup(TrapGroup group)
{
    private readonly TrapGroup Group = group.Clone();

    // 不要把 IEventObj 存進集合跨幀用：本 pin 的 ObjectTable 每個格位×kind 預配一個包裝、存取時就地改寫 Address，
    // TrackedGroup 會活過整場塔戰，存包裝等於存一批會靜默換人或懸空的原生指標。
    // 這裡只需要「發現了幾個陷阱」，存 GameObjectId 純量即可。
    public readonly List<ulong> Traps = [];

    public bool HasDiscoveredAllTraps()
    {
        return Traps.Count >= Group.MaxInGroup;
    }
}
