using System;
using Ocelot.IPC;

namespace BOCCHI.Pathfinding;

/// <summary>
/// 讓位用的「Lifestream 正在執行任務」查詢，只在讓位取樣時（約每 100 毫秒）問；只在 framework 執行緒用。
/// 問不到（沒裝、還沒載入、端點擲例外）當成不忙＝與沒有這個判斷時相同，之後 <see cref="RetryMs"/> 內不再問。
/// 刻意不沿用非法模式選活動的「問不到當成忙」：那裡忙＝這次不換，這裡忙＝一直不送移動。
/// </summary>
public sealed class LifestreamBusyProbe(Lifestream? lifestream)
{
    public const long RetryMs = 5_000;

    private long quietUntil;

    public bool Query(long now)
    {
        if (lifestream == null || now < quietUntil)
        {
            return false;
        }

        try
        {
            return lifestream.IsBusy();
        }
        catch (Exception)
        {
            quietUntil = now + RetryMs;
            return false;
        }
    }
}
