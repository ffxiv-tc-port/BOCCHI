using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace BOCCHI.Modules.CriticalEncounters;

/// <summary>當幀從原生 DynamicEvent 抄出來的值；不含任何原生指標，可以跨幀保存。</summary>
/// <param name="Radius">原生 <c>DynamicEvent.Unknown4</c>，目前當作參戰範圍半徑用。</param>
/// <param name="Name">取自 DynamicEvent 表（與原生名稱同源），不讀原生字串。</param>
public readonly record struct CriticalEncounterSnapshot(
    ushort DynamicEventId,
    byte EventType,
    DynamicEventState State,
    byte Progress,
    int StartTimestamp,
    uint SecondsLeft,
    byte Participants,
    byte Radius,
    Vector3 Position,
    string Name);
