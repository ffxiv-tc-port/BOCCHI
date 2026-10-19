using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using ECommons.EzIpcManager;
using Ocelot.IPC;

namespace BOCCHI.Modules.Aggro;

/// <summary>
/// vnavmesh 的 Nav.PathfindAroundZones。宣告型別必須與提供端逐字相同：回傳型別不同時 CallGate 會走 JSON 轉換，Task 轉不過去。
/// 刻意放在 BOCCHI 而不是 Ocelot 的 VNavmesh：只有仇恨迴避用。
/// </summary>
public sealed class VnavZoneIpc() : IPCSubscriber("vnavmesh")
{
    /// <summary>(起點, 終點, 區域陣列（每筆 8 個 float）, horizon) → (路徑, 旗標, 修補段數, 避不開數)；端點不存在時擲 IpcNotReadyError。</summary>
    [EzIPC("Nav.PathfindAroundZones")]
    public readonly Func<Vector3, Vector3, float[], float, Task<(List<Vector3> Path, int Flags, int Repaired, int Unavoidable)>> PathfindAroundZones = null!;
}
