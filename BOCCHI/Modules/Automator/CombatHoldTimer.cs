namespace BOCCHI.Modules.Automator;

/// <summary>非法模式「沒有活動、還在戰鬥中所以不選下一個」的等待計時；只存時間戳。只在框架執行緒用。</summary>
public struct CombatHoldTimer
{
    private bool holding;

    private long since;

    /// <summary>每次走到選活動的戰鬥閘門呼叫；一段等待剛結束時回 true，<paramref name="heldMs"/> 是它的長度。</summary>
    public bool Step(bool blocked, long now, out long heldMs)
    {
        heldMs = 0;
        if (blocked)
        {
            if (!holding)
            {
                holding = true;
                since = now;
            }

            return false;
        }

        if (!holding)
        {
            return false;
        }

        holding = false;
        heldMs = now - since;
        return true;
    }

    public void Reset()
    {
        holding = false;
        since = 0;
    }
}
