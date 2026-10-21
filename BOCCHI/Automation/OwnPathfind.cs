using System;
using Ocelot.IPC;

namespace BOCCHI.Automation;

// 本檔契約：只在框架執行緒呼叫，不配置。
// vnavmesh 的 Nav.PathfindCancelAll 是全域的、也不記算路是誰送的：會連別的插件（Lifestream 地面導航的 Nav.Pathfind 等）
// 正在算或排隊的路一起取消，對方的整條任務就中止。所以只在能確定 vnavmesh 裡只有 BOCCHI 自己那筆算路時才叫。

/// <summary><see cref="OwnPathfind.CancelIfOnlyOwn"/> 這次怎麼處理。</summary>
public enum OwnPathfindCancel : byte
{
    /// <summary>SimpleMove 沒在算：不必取消。</summary>
    Idle,

    /// <summary>已取消。</summary>
    Cancelled,

    /// <summary>BOCCHI 沒有還沒作廢的目的地：在算的可能是別人的，不取消。</summary>
    NotOwn,

    /// <summary>vnavmesh 裡還有第二筆尋路（可能是別的插件的）：不取消。</summary>
    OthersQueued,

    /// <summary>問不到 vnavmesh：不取消。</summary>
    Unreadable,
}

public static class OwnPathfind
{
    /// <summary>SimpleMove 正在算、vnavmesh 只有這一筆尋路（Nav.PathfindNumQueued＝0），而且 BOCCHI 有還沒作廢的目的地時，才取消算路。</summary>
    /// <param name="ownDestination">BOCCHI 有還沒作廢的目的地（<see cref="OwnMoveDestinations.HasCurrent"/>，或停止前 CaptureCurrent 之後 Count &gt; 0）。</param>
    public static OwnPathfindCancel CancelIfOnlyOwn(VNavmesh vnav, bool ownDestination)
    {
        try
        {
            if (!vnav.IsSimpleMoveInProgress())
            {
                return OwnPathfindCancel.Idle;
            }

            if (!ownDestination)
            {
                return OwnPathfindCancel.NotOwn;
            }

            if (vnav.NumQueuedPathfindRequests() > 0)
            {
                return OwnPathfindCancel.OthersQueued;
            }
        }
        catch (Exception)
        {
            return OwnPathfindCancel.Unreadable;
        }

        vnav.CancelAllPathfinds();
        return OwnPathfindCancel.Cancelled;
    }

    /// <summary>給 log 用的一句話。</summary>
    public static string Describe(OwnPathfindCancel result)
    {
        return result switch
        {
            OwnPathfindCancel.Idle => "沒有在算的路",
            OwnPathfindCancel.Cancelled => "Nav.PathfindCancelAll（只有 BOCCHI 自己在算）",
            OwnPathfindCancel.NotOwn => "不取消算路（BOCCHI 沒有還在走的目的地，可能是別的插件的）",
            OwnPathfindCancel.OthersQueued => "不取消算路（vnavmesh 還有別筆尋路在排隊，可能是別的插件的）",
            _ => "不取消算路（問不到 vnavmesh）",
        };
    }
}
