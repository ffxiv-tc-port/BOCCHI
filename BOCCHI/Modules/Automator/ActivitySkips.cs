using System.Collections.Generic;

namespace BOCCHI.Modules.Automator;

/// <summary>「換一場」略過的活動種類。</summary>
public enum SkipKind : byte
{
    Fate,
    CriticalEncounter,
}

/// <summary>一場 F.A.T.E.／緊急遭遇戰：id＋開始時間（F.A.T.E. 的開始時間、緊急遭遇戰的報名截止，Unix 秒；0 以下＝還沒讀到）。</summary>
public readonly record struct EventInstance(SkipKind Kind, uint Id, int Start);

/// <summary>按「換一場」的結果。</summary>
public enum SkipOutcome : byte
{
    Skipped,

    /// <summary>沒有選定的活動（待命中）。</summary>
    NoActivity,

    /// <summary>目前是等魔法甕出現，不是一場活動。</summary>
    NotAnEvent,
}

/// <summary>略過當下角色在哪裡（決定聊天視窗怎麼說明接下來會怎樣）。</summary>
public enum SkipPlace : byte
{
    /// <summary>還沒到（趕路、等開打的站位外）：馬上改挑下一場。</summary>
    Away,

    /// <summary>在略過的那場 F.A.T.E. 範圍內：不帶角色離開，離開範圍或它結束後才選下一場。</summary>
    InsideFate,

    /// <summary>在略過的那場緊急遭遇戰裡：它結束後才選下一場。</summary>
    InsideCriticalEncounter,

    /// <summary>戰鬥中：脫離戰鬥後才選下一場。</summary>
    InCombat,
}

/// <summary>略過的那一場現在還在不在。</summary>
public enum SkipLiveness : byte
{
    /// <summary>還在（同一場）。</summary>
    Live,

    /// <summary>結束、從清單消失，或清單上已經是同一種活動的下一場。</summary>
    Gone,

    /// <summary>清單讀不到（模組沒載入、緊急遭遇戰清單是空的）：先留著。</summary>
    Unknown,
}

// 本檔契約：只在框架執行緒用；純邏輯、不碰遊戲（離線驗收直接呼叫）。
// 比對單位是「一場」：同一種活動的下一場（開始時間不同）不算略過；開始時間有一邊還沒讀到時當成同一場。

/// <summary>本場略過：非法模式這一輪不再選的場次，以及「手動停止後很快又開始＝換一場」要用的停止記錄。</summary>
public sealed class ActivitySkips
{
    /// <summary>手動停止後這段時間內又開始，停止當下選定的那一場算略過。</summary>
    public const long QuickRestartMs = 120_000;

    private readonly List<Entry> entries = [];

    private bool hasPending;

    private Entry pending;

    private long pendingAt;

    public int Count
    {
        get => entries.Count;
    }

    /// <summary>還沒用掉的停止記錄（停止當下選定的那一場）；給記錄與測試用。</summary>
    public bool HasPendingStop
    {
        get => hasPending;
    }

    public EventInstance At(int index)
    {
        return entries[index].Instance;
    }

    public string NameAt(int index)
    {
        return entries[index].Name;
    }

    public static bool SameInstance(int skippedStart, int liveStart)
    {
        return skippedStart <= 0 || liveStart <= 0 || skippedStart == liveStart;
    }

    /// <param name="listed">清單上有這個 id。</param>
    /// <param name="active">那一場在進行（緊急遭遇戰不是 Inactive；F.A.T.E. 在表上就是）。</param>
    public static SkipLiveness Judge(bool listed, bool active, int skippedStart, int liveStart)
    {
        if (!listed || !active)
        {
            return SkipLiveness.Gone;
        }

        return SameInstance(skippedStart, liveStart) ? SkipLiveness.Live : SkipLiveness.Gone;
    }

    public bool Contains(SkipKind kind, uint id, int start)
    {
        foreach (var entry in entries)
        {
            if (entry.Instance.Kind == kind && entry.Instance.Id == id && SameInstance(entry.Instance.Start, start))
            {
                return true;
            }
        }

        return false;
    }

    /// <returns>false＝已經在清單上（同一場）。</returns>
    public bool Add(EventInstance instance, string name)
    {
        if (Contains(instance.Kind, instance.Id, instance.Start))
        {
            return false;
        }

        entries.Add(new Entry(instance, name));
        return true;
    }

    public void RemoveAt(int index)
    {
        entries.RemoveAt(index);
    }

    /// <summary>略過時開始時間還沒讀到、現在讀到了：補上，之後同一種活動的下一場才分得出來。</summary>
    public void FillStart(int index, int liveStart)
    {
        var entry = entries[index];
        if (entry.Instance.Start <= 0 && liveStart > 0)
        {
            entries[index] = entry with { Instance = entry.Instance with { Start = liveStart } };
        }
    }

    /// <summary>清空略過清單與停止記錄（換區、登出、使用者清空）；回傳清掉的場數。</summary>
    public int Clear()
    {
        var count = entries.Count;
        entries.Clear();
        hasPending = false;
        return count;
    }

    /// <summary>手動停止非法模式時呼叫；<paramref name="instance"/> 是 null＝停止時沒有選定的一場（把舊的記錄一起丟掉）。</summary>
    public void NoteManualStop(EventInstance? instance, string name, long now)
    {
        hasPending = instance != null;
        if (instance is { } value)
        {
            pending = new Entry(value, name);
            pendingAt = now;
        }
    }

    /// <summary>開始非法模式時呼叫：取走停止記錄（不論多久以前），停止後 <see cref="QuickRestartMs"/> 內（含）才回 true。</summary>
    public bool TakeQuickRestart(long now, out EventInstance instance, out string name, out long elapsedMs)
    {
        instance = pending.Instance;
        name = pending.Name ?? string.Empty;
        elapsedMs = now - pendingAt;
        var had = hasPending;
        hasPending = false;
        return had && elapsedMs >= 0 && elapsedMs <= QuickRestartMs;
    }

    private readonly record struct Entry(EventInstance Instance, string Name);
}
