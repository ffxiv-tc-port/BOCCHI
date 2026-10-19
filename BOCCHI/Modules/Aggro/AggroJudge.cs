using System;
using BOCCHI.Data;

namespace BOCCHI.Modules.Aggro;

public enum AggroState : byte
{
    /// <summary>不畫：非主動、刻意排除，或關掉「顯示未知怪」。</summary>
    None = 0,

    /// <summary>淡色：資料不足以確定（讀不到知見等級或魔物等級、不在表上、感知類型不明）。</summary>
    Possible = 1,

    /// <summary>實色：依規則會主動攻擊。</summary>
    Certain = 2,
}

public enum UndeadNight : byte
{
    Unknown = 0,

    Night = 1,

    Day = 2,
}

/// <summary>
/// 一次掃描共用的判定輸入。RealKnowledgeLevel 0＝讀不到；PlayerHpFraction 負值＝讀不到。
/// Night 判定不看（夜間不死系一樣套等級門檻），保留給呼叫端與離線測試。
/// </summary>
public readonly record struct AggroContext(
    int RealKnowledgeLevel,
    float PlayerHpFraction,
    float BloodHpThreshold,
    UndeadNight Night,
    bool LevelSourceReliable,
    bool ShowUnknown);

/// <summary>純函式：不碰遊戲、不配置，離線測試直接呼叫。</summary>
public static class AggroJudge
{
    public static AggroState Judge(bool found, in AggroInfo info, int readLevel, in AggroContext ctx, out AggroSense senses, out bool bloodActive)
    {
        bloodActive = false;
        if (!found)
        {
            senses = AggroSense.Unknown;
            return ctx.ShowUnknown ? AggroState.Possible : AggroState.None;
        }

        senses = info.Senses;
        if (senses == AggroSense.None)
        {
            return AggroState.None;
        }

        if ((senses & AggroSense.Unknown) != 0)
        {
            return ctx.ShowUnknown ? AggroState.Possible : AggroState.None;
        }

        // 夜間不死系（血量型）與其他魔物一樣用等級門檻判；體力門檻只決定要不要多畫／多避血量感知那一圈。
        if ((senses & AggroSense.Blood) != 0)
        {
            bloodActive = ctx.PlayerHpFraction >= 0f && ctx.PlayerHpFraction < ctx.BloodHpThreshold;
        }

        var level = ResolveLevel(info, readLevel, ctx.LevelSourceReliable);
        if (ctx.RealKnowledgeLevel <= 0 || level <= 0)
        {
            return AggroState.Possible;
        }

        return level < ctx.RealKnowledgeLevel ? AggroState.None : AggroState.Certain;
    }

    /// <summary>回判定用的怪物等級，0＝不知道。等級來源不可靠時固定等級的怪改用表值、多等級的怪當不知道。</summary>
    public static int ResolveLevel(in AggroInfo info, int readLevel, bool levelSourceReliable)
    {
        if (!levelSourceReliable)
        {
            return info.FixedLevel;
        }

        return info.AllowsLevel(readLevel) ? readLevel : 0;
    }

    /// <summary>夜間不死系出沒時段：艾奧傑亞時間 22:34～4:00。</summary>
    public static UndeadNight UndeadNightAt(long unixMilliseconds)
    {
        if (unixMilliseconds <= 0)
        {
            return UndeadNight.Unknown;
        }

        // 艾奧傑亞時間走得是現實的 3600/175 倍（1 艾奧傑亞小時＝175 秒）。
        var eorzeaMinutes = unixMilliseconds * 144 / 7 / 60_000;
        var minuteOfDay = (int)(eorzeaMinutes % 1440);
        return minuteOfDay >= 22 * 60 + 34 || minuteOfDay < 4 * 60 ? UndeadNight.Night : UndeadNight.Day;
    }

    public static int EorzeaMinuteOfDay(long unixMilliseconds)
    {
        return unixMilliseconds <= 0 ? -1 : (int)(unixMilliseconds * 144 / 7 / 60_000 % 1440);
    }
}

/// <summary>
/// 等級來源自校準：每隻固定等級的怪（以 GameObjectId 去重）第一次讀到非 0 等級時比對表值。
/// 不符達上限且不少於相符數就判定不可靠（本次載入期間不再改回）。框架執行緒專用。
/// </summary>
public sealed class LevelCalibration
{
    public const int MismatchLimit = 3;

    public const int Capacity = 256;

    private readonly ulong[] ring = new ulong[Capacity];

    private readonly System.Collections.Generic.HashSet<ulong> sampled = new(Capacity);

    private int next;

    public int Matches { get; private set; }

    public int Mismatches { get; private set; }

    public bool Reliable { get; private set; } = true;

    public enum Outcome : byte
    {
        Skipped,
        Match,
        Mismatch,
        BecameUnreliable,
    }

    public Outcome Observe(ulong objectId, in AggroInfo info, int readLevel)
    {
        if (info.FixedLevel == 0 || readLevel == 0 || objectId == 0 || sampled.Contains(objectId))
        {
            return Outcome.Skipped;
        }

        // 先移出最舊的再加入：集合永遠不超過預配容量，穩態不配置。
        var evicted = ring[next];
        if (evicted != 0)
        {
            sampled.Remove(evicted);
        }

        sampled.Add(objectId);
        ring[next] = objectId;
        next = (next + 1) % Capacity;

        if (readLevel == info.FixedLevel)
        {
            Matches++;
            return Outcome.Match;
        }

        Mismatches++;
        if (Reliable && Mismatches >= MismatchLimit && Mismatches >= Matches)
        {
            Reliable = false;
            return Outcome.BecameUnreliable;
        }

        return Outcome.Mismatch;
    }
}
