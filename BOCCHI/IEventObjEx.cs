using System;
using Dalamud.Game.ClientState.Objects.SubKinds;

namespace BOCCHI;

/// <summary>
/// 原本住在 <c>Modules/Data</c>(遙測模組)底下,但使用者是分岔之塔的陷阱比對
/// (<c>TrapData.GetGroup</c>／<c>TowerRun</c>),與遙測無關,
/// 所以遙測整組移除時搬到與 <c>IGameObjectEx</c> 同層。
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
