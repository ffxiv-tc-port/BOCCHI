using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameFunctions;
using ECommons.GameHelpers;

namespace BOCCHI;

public static class TargetHelper
{
    public static IEnumerable<IBattleNpc> Enemies { get; private set; } = [];

    /// <summary>Update 跑過了沒：跑過之後 Enemies 才是接著物件表的查詢，之前是空的。</summary>
    public static bool HasScanned { get; private set; }

    public static void Update()
    {
        HasScanned = true;
        Enemies = Svc.Objects.OfType<IBattleNpc>()
            .Where(o => o is
            {
                IsDead: false,
                IsTargetable: true,
            }).Where(o => o.IsHostile())
            .OrderBy(Player.DistanceTo);
    }
}
