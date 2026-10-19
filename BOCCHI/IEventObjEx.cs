using System;
using Dalamud.Game.ClientState.Objects.SubKinds;

namespace BOCCHI;

/// <summary>
/// 分岔之塔陷阱比對(<c>TrapData.GetGroup</c>／<c>TowerRun</c>)用的 IEventObj 鍵。
/// </summary>
public static class IEventObjEx
{
    public static string GetKey(this IEventObj obj)
    {
        var x = (float)Math.Round(obj.Position.X, 2);
        var y = (float)Math.Round(obj.Position.Y, 2);
        var z = (float)Math.Round(obj.Position.Z, 2);

        return $"{obj.BaseId}:{x:F2},{y:F2},{z:F2}";
    }
}
