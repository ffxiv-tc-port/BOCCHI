using System;
using System.Globalization;

namespace BOCCHI.Automation;

// 本檔契約：純計數與字樣，不碰遊戲（離線測試直接呼叫）。只在框架執行緒讀寫；時間一律 Environment.TickCount64 的毫秒。
// 摘要行的前綴 [BOCCHI][目標] 與欄位名是 target_switch_report.py --attr 的比對對象，改名要兩邊一起改。

/// <summary>一個活動（或一次自動打怪）裡 BOCCHI 改寫目標的統計。</summary>
public sealed class TargetStats
{
    public const string Tag = "[BOCCHI][目標]";

    /// <summary>BOCCHI 改成另一隻（含從沒有目標到有）。</summary>
    public int Changes { get; private set; }

    /// <summary>BOCCHI 把目標清空（只有維持秒數 0 的舊行為會）。</summary>
    public int Clears { get; private set; }

    /// <summary>BOCCHI 寫的目標被別人（其他插件或你自己）換掉。</summary>
    public int TakenAway { get; private set; }

    /// <summary>舊邏輯這一次會改寫、遲滯規則沒有寫。</summary>
    public int Kept { get; private set; }

    /// <summary>BOCCHI 兩次改寫之間最短的間隔；−1＝不到兩次。</summary>
    public long MinIntervalMs { get; private set; } = -1;

    public bool EarlyEngaged { get; set; }

    public int EarlyEngageFallBacks { get; set; }

    private long lastWriteAt;

    private ulong lastObservedId;

    public void Reset()
    {
        Changes = 0;
        Clears = 0;
        TakenAway = 0;
        Kept = 0;
        MinIntervalMs = -1;
        EarlyEngaged = false;
        EarlyEngageFallBacks = 0;
        lastWriteAt = 0;
        lastObservedId = 0;
    }

    /// <summary>看到目前目標（每次判斷前呼叫）：從 BOCCHI 寫的那隻變成別的，就算一次被改走。</summary>
    public void Observe(ulong currentId, ulong lastWrittenId)
    {
        if (currentId == lastObservedId)
        {
            return;
        }

        if (lastObservedId != 0 && lastObservedId == lastWrittenId && currentId != lastWrittenId)
        {
            TakenAway++;
        }

        lastObservedId = currentId;
    }

    public void NoteDecision(in TargetDecision decision, ulong currentId)
    {
        if (!decision.Writes && decision.LegacyId != currentId)
        {
            Kept++;
        }
    }

    /// <summary>BOCCHI 真的把目標改成 to（0＝清空）。</summary>
    public void NoteWrite(ulong to, long now)
    {
        if (to == 0)
        {
            Clears++;
        }
        else
        {
            Changes++;
        }

        if (lastWriteAt != 0)
        {
            var interval = now - lastWriteAt;
            MinIntervalMs = MinIntervalMs < 0 ? interval : Math.Min(MinIntervalMs, interval);
        }

        lastWriteAt = now;
        lastObservedId = to;
    }

    /// <summary>活動結束時的一行摘要（Information）。</summary>
    public string Summary(string activity, string name, string end, int holdSeconds)
    {
        var interval = MinIntervalMs < 0 ? "-" : (MinIntervalMs / 1000f).ToString("F1", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture,
            $"{Tag} 活動={activity} 名=「{name}」 結束={end} 改目標={Changes} 清空={Clears} 被別人改走={TakenAway} 最短間隔={interval} "
            + $"保留(遲滯)={Kept} 提早接戰={(EarlyEngaged ? "是" : "否")} 提早接戰退回={EarlyEngageFallBacks} 維持秒數={holdSeconds}");
    }

    /// <summary>
    /// 每次真的改目標時的一行（Debug）；id 印 8 位十六進位，0 印「無」。
    /// 舊 id 刻意印小寫：統計工具只把大寫的 4XXXXXXX 當成這一行的目標，換回舊目標的那一發才不會被算成 BOCCHI 換的。
    /// </summary>
    public static string ChangeLine(ulong from, ulong to, string reason, string pick, string source)
    {
        return $"{Tag} 換 舊={Id(from, "x8")} 新={(to == 0 ? "清空" : Id(to, "X8"))} 原因={reason} 挑法={pick} 來源={source}";
    }

    private static string Id(ulong id, string format)
    {
        return id == 0 ? "無" : id.ToString(format, CultureInfo.InvariantCulture);
    }
}
