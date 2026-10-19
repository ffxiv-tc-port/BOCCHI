using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.DalamudServices;
using ECommons.GameHelpers;

namespace BOCCHI.Modules.Treasure;

public class TreasureTracker
{
    public List<Treasure> Treasures { get; private set; } = [];

    public void Tick(Plugin plugin)
    {
        // 用 GameObjectId 當鍵,不要用 DataId:
        // (1) 同型寶箱共用 DataId,ToDictionary 遇到重複鍵會丟 ArgumentException,
        //     而這裡每幀都跑 → 整個 Treasure 模組會靜默失效。
        // (2) 用 DataId 當鍵時,已消失寶箱的陳舊項目會被「同型的另一個寶箱」保住,
        //     接著 CheckOpened() 就去解參考已釋放的位址。
        var treasures = Svc.Objects
            .Where(o => o is { ObjectKind: ObjectKind.Treasure })
            .ToDictionary(o => o.GameObjectId, o => o);

        var knownIds = Treasures.Select(t => t.Id).ToHashSet();

        // Removed
        for (var i = Treasures.Count - 1; i >= 0; i--)
        {
            var treasure = Treasures[i];
            if (!treasures.ContainsKey(treasure.Id) || !treasure.IsValid())
            {
                Treasures.RemoveAt(i);
            }
        }

        // Added
        foreach (var (objectId, obj) in treasures)
        {
            if (knownIds.Contains(objectId))
            {
                continue;
            }

            var treasure = new Treasure(obj);
            if (treasure.IsValid())
            {
                Treasures.Add(treasure);
            }
        }

        Treasures = Treasures.OrderBy(t => Player.DistanceTo(t.GetPosition())).ToList();

        // 每幀都要查：Treasure.IsOpened 讀的是這裡更新的旗標（寶箱記錄、開箱計數都靠它）。
        foreach (var treasure in Treasures)
        {
            treasure.CheckOpened();
        }
    }
}
