using System.Numerics;

namespace BOCCHI.Pathfinding;

/// <summary>
/// 可序列化的座標：尋路節點資料(<c>NodeDataSchema</c>)與偵錯面板的路徑匯出用。
/// 屬性名稱(X/Y/Z)刻意不動 —— <c>Data/*.json</c> 的節點距離表是照這個結構序列化的。
/// </summary>
public struct Position
{
    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }


    public static Position Create(Vector3 vector3)
    {
        return new Position
        {
            X = vector3.X,
            Y = vector3.Y,
            Z = vector3.Z,
        };
    }
}
