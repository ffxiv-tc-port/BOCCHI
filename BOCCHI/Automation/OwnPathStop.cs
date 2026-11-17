using System;
using System.Numerics;
using Ocelot.IPC;

namespace BOCCHI.Automation;

// 本檔契約：只在框架執行緒呼叫。
// Path.Stop 是全域的（會停掉別的插件或使用者送的路線），所以自動化途中的停止點用路線終點判斷是不是 BOCCHI 送的再停。

public static class OwnPathStop
{
    /// <summary>vnavmesh 正在走的路線是不是 <paramref name="own"/> 裡的目的地；<paramref name="end"/>＝路線終點（讀不到是 null）。問不到 vnavmesh 回 Unknown。</summary>
    public static OwnPathVerdict Judge(VNavmesh vnav, OwnMoveDestinations own, out Vector3? end)
    {
        end = null;
        try
        {
            if (!vnav.IsRunning())
            {
                return OwnPathVerdict.Idle;
            }

            var waypoints = vnav.GetWaypoints();
            if (waypoints is { Count: > 0 })
            {
                end = waypoints[^1];
            }
        }
        catch (Exception)
        {
            end = null;
            return OwnPathVerdict.Unknown;
        }

        return own.Judge(true, end);
    }

    /// <summary>只停 BOCCHI 自己的路線：Own／Unknown 停（讀不到終點照 AutomationControl 的慣例當自己的），Idle／Foreign 不停。回傳判定給呼叫端寫 log。</summary>
    public static OwnPathVerdict StopIfOwn(VNavmesh vnav, OwnMoveDestinations own, out Vector3? end)
    {
        var verdict = Judge(vnav, own, out end);
        if (verdict is OwnPathVerdict.Own or OwnPathVerdict.Unknown)
        {
            vnav.Stop();
        }

        return verdict;
    }
}
