namespace BOCCHI.Modules.Automator;

/// <summary>已在 F.A.T.E. 裡、沒有進行中的活動時，接手哪一場。</summary>
public enum FateResumeChoice : byte
{
    /// <summary>接手角色所在的那一場。</summary>
    Current,

    /// <summary>所在的那一場沒勾選參加：不接手，也不改去別場（照舊不參加沒勾選的）。</summary>
    NotEnabled,

    /// <summary>所在的那一場不在 BOCCHI 的 F.A.T.E. 表上：不接手，也不改去別場。</summary>
    NotTracked,

    /// <summary>讀不到所在的那一場：照舊取表上第一個有勾選的。</summary>
    Unknown,

    /// <summary>所在的那一場被「換一場」略過：不接手，也不改去別場（不帶角色離開範圍）。</summary>
    Skipped,
}

/// <summary>純邏輯，不碰遊戲（離線驗收直接呼叫）。</summary>
public static class FateResumeRules
{
    /// <param name="currentFateId">角色所在的 F.A.T.E.（FateManager.CurrentFate）；0＝讀不到。</param>
    /// <param name="tracked">那一場在 BOCCHI 的 F.A.T.E. 表上。</param>
    /// <param name="enabled">那一場有勾選參加。</param>
    public static FateResumeChoice Decide(uint currentFateId, bool tracked, bool enabled)
    {
        if (currentFateId == 0)
        {
            return FateResumeChoice.Unknown;
        }

        if (!tracked)
        {
            return FateResumeChoice.NotTracked;
        }

        return enabled ? FateResumeChoice.Current : FateResumeChoice.NotEnabled;
    }

    /// <summary>「同一個理由、同一場」只寫一次記錄用的鍵；不會是 0（0＝還沒寫過）。</summary>
    public static ulong NoteKey(FateResumeChoice choice, uint fateId, int startEpoch)
    {
        return ((ulong)((byte)choice + 1) << 56) | ((ulong)(fateId & 0xFFFFFF) << 32) | (uint)startEpoch;
    }
}
