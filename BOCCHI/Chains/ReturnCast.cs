namespace BOCCHI.Chains;

public enum ReturnCastKind
{
    None,
    Return,
    Mount,
    Other,
}

public enum ReturnStage
{
    Dismount,
    WaitCooldown,
    WaitCastStart,
    Casting,
    WaitLoadingStart,
    Loading,
    Done,
}

/// <summary>返回動作鏈的判斷：純邏輯、不碰遊戲（離線驗收直接呼叫）。</summary>
public static class ReturnCast
{
    /// <summary>詠唱資訊的 ActionType：1＝Action 表的技能、13＝坐騎。</summary>
    public const byte ActionTypeAction = 1;

    public const byte ActionTypeMount = 13;

    /// <summary>Action 表：6＝返回、41343＝亞返回（新月島裡按「返回」實際詠唱的技能）。</summary>
    public const uint ReturnActionId = 6;

    public const uint OccultReturnActionId = 41343;

    public const long DismountGiveUpMs = 3000;

    public const long DismountRetryMs = 1000;

    /// <summary>送出後這麼久還沒確認框、沒詠唱才補送：成功時確認框約 0.1～0.2 秒就出現。</summary>
    public const long ResendAfterMs = 1000;

    /// <summary>公共冷卻轉完要先靜這麼久：遊戲排進佇列的那一發會在冷卻轉完時自己送出，不跟它搶。</summary>
    public const long ResendGcdQuietMs = 300;

    public const int MaxResends = 2;

    public static ReturnCastKind Classify(bool casting, byte type, uint id)
    {
        if (!casting)
        {
            return ReturnCastKind.None;
        }

        if (type == ActionTypeAction && id is ReturnActionId or OccultReturnActionId)
        {
            return ReturnCastKind.Return;
        }

        return type == ActionTypeMount ? ReturnCastKind.Mount : ReturnCastKind.Other;
    }

    /// <summary>失敗記錄要記的那一個詠唱：看過返回就記返回，否則記第一個看到的別的詠唱。</summary>
    public static ReturnCastKind Remember(ReturnCastKind remembered, ReturnCastKind seen)
    {
        if (seen == ReturnCastKind.None || remembered == ReturnCastKind.Return)
        {
            return remembered;
        }

        return seen == ReturnCastKind.Return || remembered == ReturnCastKind.None ? seen : remembered;
    }

    /// <summary>
    /// 下坐騎那一步：沒騎乘也不在上下坐騎動畫裡就往下；騎著時每 <see cref="DismountRetryMs"/> 試一次。
    /// 超過 <see cref="DismountGiveUpMs"/> 還沒下來就照樣往下施放，不讓整次返回失敗。
    /// </summary>
    /// <returns>true＝這一步完成。</returns>
    public static bool DismountStep(bool mounted, bool transition, bool castingMount, long elapsedMs, long sinceLastTryMs, out bool tryNow, out bool gaveUp)
    {
        tryNow = false;
        gaveUp = false;
        if (!mounted && !transition && !castingMount)
        {
            return true;
        }

        if (elapsedMs >= DismountGiveUpMs)
        {
            gaveUp = true;
            return true;
        }

        tryNow = mounted && !transition && sinceLastTryMs >= DismountRetryMs;
        return false;
    }

    /// <summary>
    /// 等詠唱開始時要不要再送一次返回：新月島的「亞返回」走公共冷卻，戰鬥 AI 剛打完一招時送出會被遊戲靜靜丟掉。
    /// 只在公共冷卻已轉完、沒在詠唱任何東西、確認框沒出現、角色沒移動、不在戰鬥也沒騎乘時補送。
    /// </summary>
    /// <param name="gcdQuietMs">公共冷卻轉完到現在的毫秒數；還在轉是 -1。</param>
    public static bool ShouldResend(long sinceSendMs, long gcdQuietMs, bool castingAnything, bool promptSeen, bool moved, bool combat, bool mounted, int resends)
    {
        return resends < MaxResends && sinceSendMs >= ResendAfterMs && gcdQuietMs >= ResendGcdQuietMs
               && !castingAnything && !promptSeen && !moved && !combat && !mounted;
    }

    /// <summary>沒看到讀取就被收掉的那幾個階段才算返回失敗；已經在讀取或跑完的不算。</summary>
    public static bool IsFailureStage(ReturnStage stage)
    {
        return stage is not (ReturnStage.Loading or ReturnStage.Done);
    }

    /// <param name="limitMs">這一階段的步驟時間上限；≤ 0＝這一步沒有自己的上限。</param>
    public static string Ending(bool dead, long stageElapsedMs, long limitMs)
    {
        if (dead)
        {
            return "倒地";
        }

        return limitMs > 0 && stageElapsedMs >= limitMs - 100 ? "逾時" : "中止";
    }

    public static string Label(ReturnCastKind kind)
    {
        return kind switch
        {
            ReturnCastKind.Return => "返回",
            ReturnCastKind.Mount => "坐騎",
            ReturnCastKind.Other => "其他",
            _ => "無",
        };
    }

    public static string Label(ReturnStage stage)
    {
        return stage switch
        {
            ReturnStage.Dismount => "下坐騎",
            ReturnStage.WaitCooldown => "等冷卻",
            ReturnStage.WaitCastStart => "等詠唱開始",
            ReturnStage.Casting => "詠唱中",
            ReturnStage.WaitLoadingStart => "等讀取開始",
            ReturnStage.Loading => "讀取中",
            _ => "完成",
        };
    }
}
