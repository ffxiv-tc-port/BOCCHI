using System;
using System.Numerics;

namespace BOCCHI.Modules.Automator;

public enum SilverChestVerdict
{
    Go,

    /// <summary>已經接近要去的活動，不中途轉去開。</summary>
    Arriving,

    /// <summary>寶箱離玩家（直線）超過上限。</summary>
    TooFar,

    /// <summary>比直接前往活動多走的距離超過上限。</summary>
    DetourTooLong,

    /// <summary>有要參加的緊急遭遇戰快截止報名（或讀不到剩餘時間）。</summary>
    EncounterClosing,

    /// <summary>寶箱已經開了（開過的寶箱還選得到好幾秒）：不去，也不再互動。</summary>
    AlreadyOpened,

    /// <summary>趕路正在讓位給你：先不去，接續趕路之後照原本的規則再看。</summary>
    PlayerSteering,
}

/// <summary>開箱動作鏈跑完後怎麼記：寶箱的狀態加上人有沒有走到能互動的距離。</summary>
public enum SilverChestOutcome
{
    Opened,

    Gone,

    /// <summary>人沒走到，寶箱卻已開啟：多半是別人開的。</summary>
    OpenedByOthers,

    GoneUnreached,

    /// <summary>人沒走到、寶箱還在：沒有互動過。</summary>
    NotReached,

    /// <summary>人在旁邊互動過，寶箱還在。</summary>
    StillThere,
}

/// <summary>非法模式兩個活動之間要不要順路去開白銀寶箱；只看傳進來的數字（離線驗收直接呼叫）。</summary>
public static class SilverChestDetourRules
{
    /// <summary>勾選參加、正在報名的緊急遭遇戰剩餘報名秒數少於這個值就不去：開箱最多約一分鐘，還要留時間趕過去。</summary>
    public const long MinEncounterSecondsLeft = 90;

    /// <summary>離活動中心不超過「範圍半徑＋這個距離」就算快到了。</summary>
    public const float ArrivingMargin = 20f;

    /// <param name="destination">null＝沒有要去的下一個活動，只看寶箱離玩家多遠。</param>
    /// <param name="distance">玩家到寶箱的直線距離。</param>
    /// <param name="detour">先繞去寶箱比直接前往目的地多走的直線距離；沒有目的地時是 0。</param>
    public static SilverChestVerdict Evaluate(
        Vector3 player,
        Vector3 chest,
        float maxDistance,
        float maxDetour,
        Vector3? destination,
        float destinationRadius,
        out float distance,
        out float detour)
    {
        distance = Vector3.Distance(player, chest);
        detour = 0f;

        if (destination is { } target)
        {
            var direct = Vector3.Distance(player, target);
            detour = distance + Vector3.Distance(chest, target) - direct;
            if (direct <= destinationRadius + ArrivingMargin)
            {
                return SilverChestVerdict.Arriving;
            }
        }

        if (distance > maxDistance)
        {
            return SilverChestVerdict.TooFar;
        }

        if (destination != null && detour > maxDetour)
        {
            return SilverChestVerdict.DetourTooLong;
        }

        return SilverChestVerdict.Go;
    }

    /// <param name="soonestSecondsLeft">勾選參加、正在報名的緊急遭遇戰裡最少的剩餘報名秒數；沒有這種場次是 null，讀不到是 -1。</param>
    public static bool EncounterBlocks(long? soonestSecondsLeft)
    {
        return soonestSecondsLeft is { } left && left < MinEncounterSecondsLeft;
    }

    /// <summary>走到寶箱的步驟上限：100 米內 40 秒，更遠的按距離放寬（每 100 米 40 秒）。</summary>
    public static int WalkTimeLimitMs(float distance)
    {
        const int baseMs = 40_000;
        const float baseDistance = 100f;
        if (!(distance > baseDistance))
        {
            return baseMs;
        }

        return (int)MathF.Ceiling(baseMs * MathF.Min(distance, 1000f) / baseDistance);
    }

    /// <summary>開箱步驟只在 2 米內互動；多留一點量測誤差，超過就當成沒走到。</summary>
    public const float ReachedDistance = 2.5f;

    /// <param name="playerDistance">人到寶箱的距離；讀不到（負數）當成沒走到。</param>
    public static SilverChestOutcome Judge(bool gone, bool opened, float playerDistance)
    {
        var reached = playerDistance >= 0f && playerDistance <= ReachedDistance;
        if (opened)
        {
            return reached ? SilverChestOutcome.Opened : SilverChestOutcome.OpenedByOthers;
        }

        if (gone)
        {
            return reached ? SilverChestOutcome.Gone : SilverChestOutcome.GoneUnreached;
        }

        return reached ? SilverChestOutcome.StillThere : SilverChestOutcome.NotReached;
    }

    /// <param name="startTimestamp">緊急遭遇戰開始（報名截止）的 Unix 秒；≤ 0＝讀不到。</param>
    /// <returns>剩餘報名秒數；讀不到回 -1。</returns>
    public static long SecondsLeft(long startTimestamp, long nowUnixSeconds)
    {
        return startTimestamp > 0 ? startTimestamp - nowUnixSeconds : -1;
    }
}
