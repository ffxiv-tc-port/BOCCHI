using System.Collections.Generic;

namespace BOCCHI.Pathfinding;

public enum HuntProgressState
{
    /// <summary>沒有進度（沒跑過、跑完、放棄）。</summary>
    None,

    /// <summary>巡迴正在跑，走過的點記在這裡。</summary>
    Active,

    /// <summary>停下了但保留進度，可以續跑。</summary>
    Paused,

    /// <summary>保留著但不能續跑（原因在 <see cref="HuntProgress.Invalidation"/>），只能從頭開始或放棄。</summary>
    Invalid,
}

public enum HuntProgressInvalidation
{
    None,

    /// <summary>換過區域（含離開新月島再進來）：寶箱與胡蘿蔔已不是同一批。</summary>
    ZoneChanged,

    LoggedOut,

    /// <summary>暫停超過續跑有效時間：跑過的點可能已經重新出現。</summary>
    Expired,
}

/// <summary>
/// 寶箱／幸運胡蘿蔔巡迴的進度：哪些點已經跑過，以及暫停後能不能續跑。純邏輯、不碰遊戲、沒有鎖，只在 framework 執行緒用。
/// 「跑過」＝那一步的處理器回報完成（<see cref="Reach"/>），而且它送出的互動動作鏈也跑完了（<see cref="CommitReached"/>）；
/// 互動還沒跑完就暫停的那個點不算跑過，續跑時會再去一次。
/// 時間一律是呼叫端給的毫秒數（Environment.TickCount64）；expiryMs ≤ 0＝不限時。
/// </summary>
public sealed class HuntProgress
{
    private readonly HashSet<uint> visited = [];

    private readonly HashSet<uint> planned = [];

    private uint reached;

    private bool hasReached;

    public HuntProgressState State { get; private set; }

    public HuntProgressInvalidation Invalidation { get; private set; }

    public long PausedAt { get; private set; }

    public int VisitedCount
    {
        get => visited.Count;
    }

    /// <summary>規劃過的點數（含已跑過的）；0＝還沒規劃完就停了，數字不可信。</summary>
    public int PlannedCount
    {
        get => planned.Count;
    }

    /// <summary>有停下來保留著的進度（可續跑或已失效）。</summary>
    public bool HasSaved
    {
        get => State is HuntProgressState.Paused or HuntProgressState.Invalid;
    }

    public bool IsVisited(uint node)
    {
        return visited.Contains(node);
    }

    public bool IsPlanned(uint node)
    {
        return planned.Contains(node);
    }

    /// <summary>從頭開始：丟掉之前的進度。</summary>
    public void BeginFresh()
    {
        visited.Clear();
        planned.Clear();
        hasReached = false;
        State = HuntProgressState.Active;
        Invalidation = HuntProgressInvalidation.None;
    }

    public void Clear()
    {
        visited.Clear();
        planned.Clear();
        hasReached = false;
        State = HuntProgressState.None;
        Invalidation = HuntProgressInvalidation.None;
    }

    /// <summary>跑過的點全部作廢、狀態不變：暫停中照樣能續跑，續跑時範圍內的點全部重新規劃。</summary>
    /// <returns>true＝原本有跑過的點。</returns>
    public bool ForgetVisited()
    {
        if (visited.Count == 0)
        {
            return false;
        }

        visited.Clear();
        return true;
    }

    public void AddPlanned(IEnumerable<uint> nodes)
    {
        planned.UnionWith(nodes);
    }

    /// <summary>處理器回報走到這個點；要等 <see cref="CommitReached"/> 才算跑過。</summary>
    public void Reach(uint node)
    {
        if (State != HuntProgressState.Active)
        {
            return;
        }

        CommitReached();
        reached = node;
        hasReached = true;
    }

    /// <summary>這一步送出的動作鏈都跑完了：走到的那個點算跑過。</summary>
    public void CommitReached()
    {
        if (!hasReached)
        {
            return;
        }

        hasReached = false;
        if (State != HuntProgressState.Active)
        {
            return;
        }

        visited.Add(reached);
        planned.Add(reached);
    }

    /// <returns>true＝從 Active 變成 Paused；走到但互動還沒跑完的那個點丟掉。</returns>
    public bool Pause(long now)
    {
        if (State != HuntProgressState.Active)
        {
            return false;
        }

        hasReached = false;
        State = HuntProgressState.Paused;
        PausedAt = now;
        return true;
    }

    /// <returns>true＝這次才從 Paused 變成 Invalid。只動暫停中的進度：正在跑的要先停下來（停止一律先於作廢）。</returns>
    public bool Invalidate(HuntProgressInvalidation why)
    {
        if (State != HuntProgressState.Paused || why == HuntProgressInvalidation.None)
        {
            return false;
        }

        State = HuntProgressState.Invalid;
        Invalidation = why;
        return true;
    }

    /// <returns>true＝這次才因為暫停太久變成 Invalid。</returns>
    public bool Expire(long now, long expiryMs)
    {
        return IsExpired(now, expiryMs) && Invalidate(HuntProgressInvalidation.Expired);
    }

    /// <param name="why">不能續跑的原因；沒有保留的進度時是 None。</param>
    public bool CanResume(long now, long expiryMs, out HuntProgressInvalidation why)
    {
        if (State == HuntProgressState.Invalid)
        {
            why = Invalidation;
            return false;
        }

        if (State != HuntProgressState.Paused)
        {
            why = HuntProgressInvalidation.None;
            return false;
        }

        why = IsExpired(now, expiryMs) ? HuntProgressInvalidation.Expired : HuntProgressInvalidation.None;
        return why == HuntProgressInvalidation.None;
    }

    public bool Resume(long now, long expiryMs)
    {
        if (!CanResume(now, expiryMs, out _))
        {
            return false;
        }

        State = HuntProgressState.Active;
        return true;
    }

    /// <summary><paramref name="nodes"/> 裡還沒跑過的點，順序不變。</summary>
    public List<uint> Remaining(List<uint> nodes)
    {
        var remaining = new List<uint>(nodes.Count);
        foreach (var node in nodes)
        {
            if (!visited.Contains(node))
            {
                remaining.Add(node);
            }
        }

        return remaining;
    }

    private bool IsExpired(long now, long expiryMs)
    {
        return State == HuntProgressState.Paused && expiryMs > 0 && now - PausedAt > expiryMs;
    }
}
