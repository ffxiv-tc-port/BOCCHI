using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace BOCCHI.Modules.CriticalEncounters;

public enum CriticalEncounterOutcome
{
    Unknown,
    Success,
    Failure,
}

/// <summary>
/// 本次進島看到的緊急遭遇戰紀錄。只在 framework 執行緒用；輸入是追蹤器每幀抄好的受管快照，不碰原生記憶體。
/// 自己逐幀判斷狀態轉換、不靠追蹤器的事件：進島時已在進行的場次也記得到。
/// 成敗是推論（遊戲不直接給）：看到結束、而且結束前一刻有在更新時，最後讀到的剩餘秒數 &gt; <see cref="UnclearSecondsLeftMax"/> 記成功、
/// ＝0 記失敗、介於中間不明（逾時失敗時遊戲可能在倒數歸零前就收場）；其餘不明。
/// 成功時進度常停在 99% 或更低（遊戲在進度到 100 之前就收場），所以不看進度。
/// </summary>
public sealed class CriticalEncounterHistory
{
    public const int Capacity = 64;

    /// <summary>最後讀到的剩餘秒數在 1～這個值之間結束：成敗不明（不算成功、不誇獎）。實機成功樣本剩的秒數都大得多。</summary>
    public const uint UnclearSecondsLeftMax = 5;

    public sealed class Entry
    {
        public ushort Id;

        public string Name = string.Empty;

        public DateTime FirstSeenUtc;

        /// <summary>Register＝從開放報名就看到；其他＝第一次看到時已經過了報名。</summary>
        public DynamicEventState FirstSeenState;

        public DynamicEventState LastState;

        public DateTime? BattleStartUtc;

        /// <summary>null＝還沒看到結束（進行中，或離島／停止更新時沒看到）。</summary>
        public DateTime? EndUtc;

        /// <summary>戰鬥中最後讀到的進度；沒進入戰鬥就是 0。</summary>
        public byte LastProgress;

        /// <summary>戰鬥中最後讀到的剩餘秒數。</summary>
        public uint LastSecondsLeft;

        public byte MaxParticipants;

        /// <summary>結束前一刻有一段沒在更新（死亡、過場、對話、讀取中）：最後讀到的數字不一定是結束時的，成敗不明。</summary>
        public bool Unobserved;

        /// <summary>角色參加了這一場（看過遊戲的「目前參加中的緊急遭遇戰」是它）。</summary>
        public bool Participated;

        public bool SawBattle
        {
            get => BattleStartUtc != null;
        }

        public CriticalEncounterOutcome Outcome
        {
            get => Judge(SawBattle, EndUtc != null, Unobserved, LastSecondsLeft);
        }

        public bool Completed
        {
            get => Outcome == CriticalEncounterOutcome.Success;
        }

        /// <summary>有看到結束、結束前一刻有在更新，但只剩 1～<see cref="UnclearSecondsLeftMax"/> 秒：不明的原因是剩的秒數，不是沒在更新。</summary>
        public bool EndedNearZero
        {
            get => SawBattle && EndUtc != null && !Unobserved && LastSecondsLeft is > 0 and <= UnclearSecondsLeftMax;
        }
    }

    // 舊→新。
    private readonly List<Entry> entries = new(Capacity);

    private readonly Dictionary<ushort, Entry> open = new();

    // 兩次 Observe 隔超過這麼久，就當成中間有一段沒看到。
    private static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(3);

    private DateTime lastObserveUtc;

    /// <summary>一場結束（看到它回到 Inactive）時呼叫。</summary>
    public Action<Entry>? OnClosed;

    public IReadOnlyList<Entry> Entries
    {
        get => entries;
    }

    public void Reset()
    {
        entries.Clear();
        open.Clear();
        lastObserveUtc = default;
    }

    /// <summary>這一場目前開著的記錄（還沒看到它回到 Inactive）；只在 framework 執行緒呼叫。</summary>
    public bool TryGetOpen(ushort id, out Entry entry)
    {
        return open.TryGetValue(id, out entry!);
    }

    /// <summary>成敗的推論；純函式（離線驗收直接呼叫）。</summary>
    public static CriticalEncounterOutcome Judge(bool sawBattle, bool ended, bool endUnobserved, uint lastSecondsLeft)
    {
        if (!sawBattle || !ended || endUnobserved)
        {
            return CriticalEncounterOutcome.Unknown;
        }

        if (lastSecondsLeft == 0)
        {
            return CriticalEncounterOutcome.Failure;
        }

        return lastSecondsLeft > UnclearSecondsLeftMax ? CriticalEncounterOutcome.Success : CriticalEncounterOutcome.Unknown;
    }

    /// <summary>結束的這一場要不要請塔塔露誇一句：角色有參加、而且推論成功（失敗與不明都不叫）；純函式。</summary>
    public static bool ShouldPraise(Entry entry)
    {
        return entry.Participated && entry.Outcome == CriticalEncounterOutcome.Success;
    }

    public void Observe(Dictionary<uint, CriticalEncounterSnapshot> current, DateTime nowUtc)
    {
        Observe(current, nowUtc, 0);
    }

    /// <param name="currentEventId">遊戲的「目前參加中的緊急遭遇戰」；0＝沒有或讀不到。</param>
    public void Observe(Dictionary<uint, CriticalEncounterSnapshot> current, DateTime nowUtc, ushort currentEventId)
    {
        // 只有「跨過結束」的空檔才影響結果：進場傳送、戰鬥中死亡這類中途的空檔不算。
        var gap = lastObserveUtc != default && nowUtc - lastObserveUtc > MaxGap;
        lastObserveUtc = nowUtc;

        foreach (var ev in current.Values)
        {
            if (ev.State == DynamicEventState.Inactive)
            {
                if (open.Remove(ev.DynamicEventId, out var ended))
                {
                    ended.LastState = DynamicEventState.Inactive;
                    ended.EndUtc = nowUtc;
                    ended.Unobserved = gap;
                    OnClosed?.Invoke(ended);
                }

                continue;
            }

            if (!open.TryGetValue(ev.DynamicEventId, out var entry))
            {
                entry = new Entry
                {
                    Id = ev.DynamicEventId,
                    Name = ev.Name,
                    FirstSeenUtc = nowUtc,
                    FirstSeenState = ev.State,
                };
                open[ev.DynamicEventId] = entry;
                Add(entry);
            }

            entry.LastState = ev.State;
            entry.MaxParticipants = Math.Max(entry.MaxParticipants, ev.Participants);
            if (currentEventId != 0 && ev.DynamicEventId == currentEventId)
            {
                entry.Participated = true;
            }

            if (ev.State != DynamicEventState.Battle)
            {
                continue;
            }

            entry.BattleStartUtc ??= nowUtc;
            entry.LastSecondsLeft = ev.SecondsLeft;
            if (ev.Progress > 0)
            {
                entry.LastProgress = ev.Progress;
            }
        }
    }

    private void Add(Entry entry)
    {
        if (entries.Count >= Capacity)
        {
            // 丟最舊的已結束場次；全都還開著（不會發生）就丟最舊的。
            var index = entries.FindIndex(static e => e.EndUtc != null);
            entries.RemoveAt(index >= 0 ? index : 0);
        }

        entries.Add(entry);
    }
}
