using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BOCCHI.Ledger;

public enum IncomeKind
{
    Silver,
    Gold,
    PhantomExp,
    Knowledge,
    KnowledgeLost,
}

/// <summary>一個活動為什麼結束；活動帳的「結束=」欄位。</summary>
public enum ActivityEnd
{
    Completed,
    Vanished,
    SwitchedToCriticalEncounter,
    Downed,
    ManualStop,
    Unloaded,
    ZoneChanged,
    LoggedOut,
    SwitchedToPot,

    /// <summary>使用者按「換一場」略過這一場（非法模式沒有停）。</summary>
    Skipped,
}

/// <summary>活動是怎麼來的；活動帳與選活動記錄的「來源=」欄位。</summary>
public enum ActivitySource
{
    /// <summary>非法模式自己挑的。</summary>
    Selected,

    /// <summary>已經在 F.A.T.E.／緊急遭遇戰裡，接手那一場。</summary>
    Resumed,

    /// <summary>前往 F.A.T.E. 途中改去緊急遭遇戰。</summary>
    SwitchedToCriticalEncounter,

    /// <summary>設定「魔法甕優先」讓魔法甕排在緊急遭遇戰之前而選到。</summary>
    PotPriority,

    /// <summary>設定「魔法甕優先」：前往別的活動途中改去魔法甕。</summary>
    SwitchedToPot,
}

/// <summary>場次帳與活動帳的純邏輯與字樣；不碰遊戲（離線驗收直接呼叫）。</summary>
public static class LedgerRules
{
    public static string Label(ActivityEnd end)
    {
        return end switch
        {
            ActivityEnd.Completed => "完成",
            ActivityEnd.Vanished => "活動消失",
            ActivityEnd.SwitchedToCriticalEncounter => "改去CE",
            ActivityEnd.Downed => "倒地",
            ActivityEnd.Unloaded => "卸載",
            ActivityEnd.ZoneChanged => "換區",
            ActivityEnd.LoggedOut => "登出",
            ActivityEnd.SwitchedToPot => "改去魔法甕",
            ActivityEnd.Skipped => "略過",
            _ => "手動停",
        };
    }

    public static string Label(ActivitySource source)
    {
        return source switch
        {
            ActivitySource.Resumed => "接手",
            ActivitySource.SwitchedToCriticalEncounter => "改去CE",
            ActivitySource.PotPriority => "魔法甕優先",
            ActivitySource.SwitchedToPot => "改去魔法甕",
            _ => "選定",
        };
    }

    /// <summary>換區、登出、卸載之後的收益不屬於這個活動：當場結算，不等。</summary>
    public static bool SettlesImmediately(ActivityEnd end)
    {
        return end is ActivityEnd.Unloaded or ActivityEnd.ZoneChanged or ActivityEnd.LoggedOut;
    }

    /// <summary>毫秒轉成秒（小數一位，不分地區）；負數或不知道寫「?」。</summary>
    public static string Seconds(long ms)
    {
        return ms < 0 ? "?" : (ms / 1000.0).ToString("F1", CultureInfo.InvariantCulture);
    }

    /// <summary>可以是負數的秒數（到帳：收益在結束之前到就是負的）。</summary>
    public static string SignedSeconds(long ms)
    {
        return (ms / 1000.0).ToString("F1", CultureInfo.InvariantCulture);
    }

    public static string Minutes(long ms)
    {
        return ms < 0 ? "?" : (ms / 60000.0).ToString("F1", CultureInfo.InvariantCulture);
    }

    /// <summary>兩個時間點的間隔；任一個沒發生（0）回 -1。</summary>
    public static long Span(long from, long to)
    {
        return from == 0 || to == 0 ? -1 : Math.Max(0, to - from);
    }

    /// <summary>知見值訊息（台服 LogMessage#10952「獲得了N點知見。」）；數字可帶千分位，加成括號可有可無。</summary>
    public const string KnowledgeTraditionalChinese = @"獲得了([\d,]+)(?:[(（]\+\d+%[)）])?點知見。";

    /// <summary>台服 LogMessage#10954「失去了N點知見……」。</summary>
    public const string KnowledgeLostTraditionalChinese = @"失去了([\d,]+)點知見";

    private static readonly Regex KnowledgeGained = new(KnowledgeTraditionalChinese, RegexOptions.Compiled);

    private static readonly Regex KnowledgeLost = new(KnowledgeLostTraditionalChinese, RegexOptions.Compiled);

    public static bool TryParseKnowledge(string text, out int amount, out bool lost)
    {
        lost = false;
        if (TryParse(KnowledgeGained.Match(text), out amount))
        {
            return true;
        }

        lost = true;
        return TryParse(KnowledgeLost.Match(text), out amount);
    }

    private static bool TryParse(Match match, out int amount)
    {
        amount = 0;
        return match.Success && int.TryParse(match.Groups[1].Value.Replace(",", string.Empty), NumberStyles.None, CultureInfo.InvariantCulture, out amount);
    }
}

/// <summary>
/// 一個活動的收益時窗：從選定開始收，結束後再收到「下一次選定活動」或「結束後 <see cref="SettleAfterEndMs"/>」，取先到者。
/// 結算由呼叫端決定（選下一個時直接結算；這裡只回答「滿 15 秒了沒」）。純邏輯；時間是 Environment.TickCount64。
/// </summary>
public sealed class ActivityIncomeWindow
{
    public const long SettleAfterEndMs = 15_000;

    public long OpenedAt { get; private set; }

    /// <summary>0＝還沒結束。</summary>
    public long EndedAt { get; private set; }

    /// <summary>最後一筆收益的時間；0＝這個時窗一筆都沒收到。</summary>
    public long LastIncomeAt { get; private set; }

    public bool IsOpen { get; private set; }

    public long Silver { get; private set; }

    public long Gold { get; private set; }

    public long PhantomExp { get; private set; }

    public long Knowledge { get; private set; }

    public long KnowledgeLost { get; private set; }

    public void Open(long now)
    {
        IsOpen = true;
        OpenedAt = now;
        EndedAt = 0;
        LastIncomeAt = 0;
        Silver = Gold = PhantomExp = Knowledge = KnowledgeLost = 0;
    }

    public void Add(IncomeKind kind, long amount, long now)
    {
        if (!IsOpen || amount <= 0)
        {
            return;
        }

        switch (kind)
        {
            case IncomeKind.Silver:
                Silver += amount;
                break;
            case IncomeKind.Gold:
                Gold += amount;
                break;
            case IncomeKind.PhantomExp:
                PhantomExp += amount;
                break;
            case IncomeKind.Knowledge:
                Knowledge += amount;
                break;
            case IncomeKind.KnowledgeLost:
                KnowledgeLost += amount;
                break;
        }

        LastIncomeAt = now;
    }

    public void End(long now)
    {
        if (IsOpen && EndedAt == 0)
        {
            EndedAt = now;
        }
    }

    public bool IsDue(long now)
    {
        return IsOpen && EndedAt != 0 && now - EndedAt >= SettleAfterEndMs;
    }

    /// <summary>最後一筆收益距結束幾毫秒（之前到是負的）；沒收到或還沒結束回 null。</summary>
    public long? LastIncomeAfterEnd()
    {
        return LastIncomeAt == 0 || EndedAt == 0 ? null : LastIncomeAt - EndedAt;
    }

    public void Close()
    {
        IsOpen = false;
    }
}
