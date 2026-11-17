using System;
using System.Numerics;
using BOCCHI.Data;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace BOCCHI.Modules.Automator;

/// <summary>
/// 設定「兩歧塔開放時到傳送魔紋等候」的資料與判斷；距離只看 X/Z。
/// 平台中心取自台服 7.3 o6b1 的 planmap.lgb：MapRange 實例（縮放 20×8×20）中心，四角另有 4 個實例在 ±9.35 米。
/// </summary>
public static class ForkedTowerPad
{
    /// <summary>兩歧塔 力之塔（DynamicEvent 48）。</summary>
    public const ushort BloodEventId = 48;

    public static readonly Vector3 BloodPad = new(63f, 126.5f, 4f);

    /// <summary>離平台中心不超過這個距離（高低差也在範圍內）就算站上平台。</summary>
    public const float ArriveM = 6f;

    public const float ArriveHeightM = 10f;

    /// <summary>等候中離平台中心超過這個距離就走回去；放寬到能走到四角的裝置旁。</summary>
    public const float LeashM = 25f;

    /// <summary>有兩歧塔狀態、又離平台這麼遠＝已經被傳送進塔（在平台上報名時可能就有那些狀態）。</summary>
    public const float InsideMinDistanceM = 150f;

    /// <summary>開打後被傳送進塔前的寬限；過了還在外面就不等了。</summary>
    public const long TeleportGraceMs = 20_000;

    /// <summary>有平台座標的兩歧塔；其他（例如台服還沒有的魔之塔）回 false。</summary>
    public static bool TryGetPad(uint eventId, out Vector3 pad)
    {
        pad = BloodPad;
        return eventId == BloodEventId;
    }

    /// <summary>還能報名（報名中或報名截止後到開打前）。</summary>
    public static bool IsRegistrationOpen(DynamicEventState state)
    {
        return state is DynamicEventState.Register or DynamicEventState.Warmup;
    }

    public static bool IsOnPad(Vector3 player, Vector3 pad)
    {
        return DistanceXZ(player, pad) <= ArriveM && MathF.Abs(player.Y - pad.Y) <= ArriveHeightM;
    }

    public static float DistanceXZ(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>已經在塔裡（不是只在平台上報名）。只在框架執行緒、拿到 <paramref name="player"/> 的這一幀呼叫；不配置。</summary>
    public static bool IsInsideTower(IGameObject player)
    {
        return ZoneData.IsInForkedTowerNoAlloc(player) && DistanceXZ(player.Position, BloodPad) > InsideMinDistanceM;
    }
}
