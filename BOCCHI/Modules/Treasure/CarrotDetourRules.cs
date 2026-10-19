using BOCCHI.Modules.Automator;
using BOCCHI.Pathfinding;

namespace BOCCHI.Modules.Treasure;

public enum CarrotVerdict
{
    Go,

    /// <summary>身上沒有幸運胡蘿蔔（去了也挖不了）。</summary>
    NoCarrot,

    /// <summary>離玩家（直線）超過上限。</summary>
    TooFar,

    InCombat,
}

public enum CarrotOutcome
{
    None,

    /// <summary>用了幸運胡蘿蔔，胡蘿蔔物件消失。</summary>
    Dug,

    /// <summary>沒用到道具胡蘿蔔就消失了（別人挖走或時間到）。</summary>
    Gone,

    /// <summary>用了幸運胡蘿蔔，等了一陣子胡蘿蔔物件還在。</summary>
    StillThere,

    /// <summary>沒用到道具、胡蘿蔔還在（動作鏈中途停了）。</summary>
    DigFailed,

    /// <summary>走過去超過時限。</summary>
    WalkTimeout,
}

/// <summary>寶箱巡迴「順路挖幸運胡蘿蔔」的判斷；只看傳進來的數字，純邏輯、不碰遊戲、不配置。</summary>
public static class CarrotDetourRules
{
    /// <summary>走到這個距離內才開始挖（同幸運胡蘿蔔巡迴）。</summary>
    public const float DigDistance = 2f;

    /// <summary>挖的動作鏈跑完後胡蘿蔔物件還在時，再等這麼久才下結論（物件可能晚幾拍才消失）。</summary>
    public const long SettleMs = 3_000;

    public static CarrotVerdict Evaluate(float distance, int maxDistance, int carrots, bool inCombat)
    {
        if (carrots <= 0)
        {
            return CarrotVerdict.NoCarrot;
        }

        if (distance > maxDistance)
        {
            return CarrotVerdict.TooFar;
        }

        return inCombat ? CarrotVerdict.InCombat : CarrotVerdict.Go;
    }

    /// <summary>走過去的上限：同非法模式順路開白銀寶箱（100 米內 40 秒，更遠每 100 米 40 秒）。</summary>
    public static int WalkTimeLimitMs(float distance)
    {
        return SilverChestDetourRules.WalkTimeLimitMs(distance);
    }

    /// <summary>還沒開始挖時每一拍：Hold＝已經在旁邊，呼叫端送出挖的動作鏈。</summary>
    public static CarrotStepAction Approach(bool present, float distance, long elapsedMs, int walkLimitMs, out CarrotOutcome outcome)
    {
        outcome = CarrotOutcome.None;
        if (!present)
        {
            outcome = CarrotOutcome.Gone;
            return CarrotStepAction.Done;
        }

        if (distance <= DigDistance)
        {
            return CarrotStepAction.Hold;
        }

        if (elapsedMs > walkLimitMs)
        {
            outcome = CarrotOutcome.WalkTimeout;
            return CarrotStepAction.Done;
        }

        return CarrotStepAction.Walk;
    }

    /// <param name="used">動作鏈看到詠唱或幸運胡蘿蔔少了一個。</param>
    /// <param name="sinceFinishedMs">動作鏈跑完（或被中止）到現在。</param>
    public static CarrotStepAction AfterDig(bool present, bool used, long sinceFinishedMs, out CarrotOutcome outcome)
    {
        outcome = CarrotOutcome.None;
        if (!present)
        {
            outcome = used ? CarrotOutcome.Dug : CarrotOutcome.Gone;
            return CarrotStepAction.Done;
        }

        if (sinceFinishedMs < SettleMs)
        {
            return CarrotStepAction.Hold;
        }

        outcome = used ? CarrotOutcome.StillThere : CarrotOutcome.DigFailed;
        return CarrotStepAction.Done;
    }
}
