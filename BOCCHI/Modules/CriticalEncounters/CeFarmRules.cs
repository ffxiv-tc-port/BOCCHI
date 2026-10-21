using System;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace BOCCHI.Modules.CriticalEncounters;

/// <summary>跟團刷 CE 目前在哪一段。</summary>
public enum CeFarmPhase : byte
{
    /// <summary>沒有武裝。</summary>
    Off,

    /// <summary>武裝中、人不在新月島：等團長排進下一個島。</summary>
    Away,

    /// <summary>在島上等目標緊急遭遇戰開放報名。</summary>
    Waiting,

    /// <summary>已經送出「跑過去」或已經在參加：等這一場結束。</summary>
    Joined,

    /// <summary>這一場成功結束：等幾秒再退出副本。</summary>
    ExitDelay,

    /// <summary>正在走介面退出副本（副本選單「退出」→ 確認框）。</summary>
    Exiting,

    /// <summary>武裝中、人在新月島，但這一輪還沒按「出發」：什麼都不做（進島不自動開始）。</summary>
    Ready,
}

/// <summary>解除武裝的原因；None＝不解除。</summary>
public enum CeFarmStop : byte
{
    None,

    Manual,

    StopAll,

    LeftParty,

    RoundsDone,

    Dead,

    WaitTimeout,

    CeFailed,

    CeUnknown,

    NotParticipated,

    JoinedTooLong,

    ExitTimeout,

    /// <summary>副本選單的排法和預期不同，或「退出」現在不能按：不按。</summary>
    ExitMenuMismatch,

    LoggedOut,

    ModuleDisabled,
}

/// <summary>跟團刷 CE 的判斷；只看傳進來的數字，純邏輯、不碰遊戲、不配置（離線驗收直接呼叫）。</summary>
public static class CeFarmRules
{
    /// <summary>看到開放報名之後等這麼久才送「跑過去」（剛進島讀圖、角色還沒站穩）。</summary>
    public const long TravelDelayMs = 5_000;

    /// <summary>這一場成功結束後等這麼久才退出（同使用者手動的節奏，獎勵與經驗值已經入帳）。</summary>
    public const long ExitDelayMs = 5_000;

    /// <summary>開始退出到離開新月島的上限。</summary>
    public const long ExitTimeLimitMs = 30_000;

    /// <summary>不在小隊要連續這麼久（讀圖中不算）才當成離開小隊：換區那幾幀小隊會短暫讀成單人。</summary>
    public const long PartyGraceMs = 15_000;

    /// <summary>送出「跑過去」或參加之後，這麼久還沒看到這一場結束就停止。</summary>
    public const long JoinedTimeLimitMs = 30 * 60_000;

    /// <summary>這一場結束時：成功而且有參加＝None（照常退出），其餘是停止的原因。沒參加優先於成敗（成敗與你無關）。</summary>
    public static CeFarmStop JudgeEnd(CriticalEncounterOutcome outcome, bool participated)
    {
        if (!participated)
        {
            return CeFarmStop.NotParticipated;
        }

        return outcome switch
        {
            CriticalEncounterOutcome.Success => CeFarmStop.None,
            CriticalEncounterOutcome.Failure => CeFarmStop.CeFailed,
            _ => CeFarmStop.CeUnknown,
        };
    }

    /// <param name="minutes">設定的等待分鐘；≤ 0 當成不限。</param>
    public static bool WaitTimedOut(long waitedMs, int minutes)
    {
        return minutes > 0 && waitedMs > minutes * 60_000L;
    }

    /// <param name="limit">設定的輪數；≤ 0＝不限。</param>
    public static bool RoundsDone(int done, int limit)
    {
        return limit > 0 && done >= limit;
    }

    public static bool ShouldTravel(DynamicEventState state, long sinceRegisterSeenMs, bool sent)
    {
        return !sent && state == DynamicEventState.Register && sinceRegisterSeenMs >= TravelDelayMs;
    }

    /// <summary>不在小隊的累計時間：讀圖中不加、回到小隊歸零。</summary>
    public static long NextAbsence(long absentMs, bool inParty, bool betweenAreas, long elapsedMs)
    {
        if (inParty)
        {
            return 0;
        }

        return betweenAreas ? absentMs : absentMs + elapsedMs;
    }

    public static bool LeftParty(long absentMs)
    {
        return absentMs >= PartyGraceMs;
    }

    // 遊戲開副本選單與刷新時填的前 17 格型別（台服 7.25）；加一顆按鈕幾乎一定多一格，對不上＝排法變了、不按。
    private static readonly AtkValueType[] MenuFingerprint =
    [
        AtkValueType.UInt,
        AtkValueType.Bool, AtkValueType.Bool, AtkValueType.Bool, AtkValueType.Bool, AtkValueType.Bool, AtkValueType.Bool, AtkValueType.Bool,
        AtkValueType.String,
        AtkValueType.Bool, AtkValueType.Bool, AtkValueType.Bool, AtkValueType.Bool, AtkValueType.Bool, AtkValueType.Bool,
        AtkValueType.UInt, AtkValueType.UInt,
    ];

    /// <summary>判斷要看的格數（型別指紋的長度）。</summary>
    public static int MenuFingerprintLength
    {
        get => MenuFingerprint.Length;
    }

    /// <summary>同一幀讀到的副本選單 AtkValues 能不能送「退出」；只有 <see cref="CeFarmMenuVerdict.Press"/> 會按。</summary>
    /// <param name="count">AtkValuesCount；AtkValues 是 null 時傳 0。</param>
    /// <param name="types">前 min(count, 17) 格的原始型別（比對時才遮 TypeMask，[8] 實際是 ManagedString）。</param>
    /// <param name="mode">[0]：0＝一般模式，1＝正在開「詳細資訊」子窗。</param>
    /// <param name="leaveEnabled">[1]：「退出」按鈕可不可按。callback 會繞過按鈕灰掉的狀態，所以一定要看。</param>
    /// <param name="badSlot">型別不符時是第幾格，其餘是 -1。</param>
    public static CeFarmMenuVerdict JudgeMenu(int count, ReadOnlySpan<AtkValueType> types, uint mode, bool leaveEnabled, out int badSlot)
    {
        badSlot = -1;
        if (count < MenuFingerprint.Length || types.Length < MenuFingerprint.Length)
        {
            return CeFarmMenuVerdict.TooFewValues;
        }

        for (var i = 0; i < MenuFingerprint.Length; i++)
        {
            if ((AtkValueType)((int)types[i] & (int)AtkValueType.TypeMask) != MenuFingerprint[i])
            {
                badSlot = i;
                return CeFarmMenuVerdict.WrongType;
            }
        }

        if (mode != 0)
        {
            return CeFarmMenuVerdict.NotNormalMode;
        }

        return leaveEnabled ? CeFarmMenuVerdict.Press : CeFarmMenuVerdict.LeaveDisabled;
    }

    /// <summary>指紋第 <paramref name="slot"/> 格該是的型別（診斷用）。</summary>
    public static AtkValueType ExpectedMenuType(int slot)
    {
        return MenuFingerprint[slot];
    }

    /// <summary>能不能武裝；不能時回原因（沒有目標、模組關著、不在小隊、有會自己移動的自動化在跑）。</summary>
    public static CeFarmArmBlock CanArm(uint target, bool moduleEnabled, bool inParty, bool movementRunning)
    {
        if (target == 0)
        {
            return CeFarmArmBlock.NoTarget;
        }

        if (!moduleEnabled)
        {
            return CeFarmArmBlock.ModuleDisabled;
        }

        if (!inParty)
        {
            return CeFarmArmBlock.NotInParty;
        }

        return movementRunning ? CeFarmArmBlock.MovementRunning : CeFarmArmBlock.None;
    }
}

/// <summary>副本選單能不能按「退出」。</summary>
public enum CeFarmMenuVerdict : byte
{
    Press,

    TooFewValues,

    WrongType,

    NotNormalMode,

    LeaveDisabled,
}

public enum CeFarmArmBlock : byte
{
    None,

    NoTarget,

    ModuleDisabled,

    NotInParty,

    MovementRunning,
}
