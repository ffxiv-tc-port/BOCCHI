using System;

namespace BOCCHI.Modules.Automator;

/// <summary>
/// 遊戲的「第二處財寶」系統訊息（LogMessage 10994「似乎能夠告知第二處財寶所在地！」）最近一次收到的時間，
/// 給非法模式與打完自動返回的財寶箱等待用。比對字串與 EurekaHelper 相同。只在框架執行緒讀寫（聊天事件也在框架執行緒）。
/// </summary>
public static class SecondTreasureNotice
{
    public const string Needle = "第二處財寶";

    public static bool Seen { get; private set; }

    /// <summary>最近一次收到的時間（Environment.TickCount64）；<see cref="Seen"/> 為 false 時不看。</summary>
    public static long SeenAt { get; private set; }

    public static void Observe(string text, long now)
    {
        if (text.Contains(Needle, StringComparison.Ordinal))
        {
            Seen = true;
            SeenAt = now;
        }
    }
}
