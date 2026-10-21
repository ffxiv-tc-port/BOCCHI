using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;

namespace BOCCHI.Pathfinding;

/// <summary>
/// 一段走路（ChainHelper.PathfindToAndWait）讓位給使用者操作的累計毫秒。到達時限與外層動作鏈的時限共用一份：
/// 讓位時間不算進時限（同趕路監看的 ExtendForYield）。只在框架執行緒用。
/// </summary>
public sealed class WalkYieldLedger
{
    public long TotalMs { get; private set; }

    public void Add(long ms)
    {
        if (ms > 0)
        {
            TotalMs += ms;
        }
    }

    /// <summary>時限 <paramref name="limitMs"/>，逾時那一刻把上次補回之後的讓位時間補回去；補了還不夠才真的逾時。</summary>
    /// <param name="what">給 log 用：哪一段（例：走向 (x, y, z)（SilverChest））。</param>
    public TaskManagerConfiguration TimeLimit(int limitMs, string what)
    {
        long taken = 0;
        return new TaskManagerConfiguration
        {
            TimeLimitMS = limitMs,
            OnTaskTimeout = (TaskManagerTask _, ref long remaining) =>
            {
                var credit = TotalMs - taken;
                taken = TotalMs;
                remaining += credit;
                if (remaining < 0)
                {
                    // 任務自帶逾時處理器時 Ocelot 不印逾時警告：真的會中止時由這裡印，等級沿用 Ocelot 的 Warning。
                    Svc.Log.Warning($"[BOCCHI][尋路讓位] {what}逾時（{limitMs / 1000} 秒，已補回讓位 {credit / 1000f:F1} 秒）：中止這一段");
                }
                else if (credit > 0)
                {
                    Svc.Log.Information($"[BOCCHI][尋路讓位] {what}到了 {limitMs / 1000} 秒時限，其中讓位 {credit / 1000f:F1} 秒不算：時限往後延 {credit / 1000f:F1} 秒");
                }
            },
        };
    }
}
