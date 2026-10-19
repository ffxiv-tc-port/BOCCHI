using System;

namespace BOCCHI.Automation;

/// <summary>
/// 因為倒地而全部停止時，停下的是哪幾項自動化（只在記憶體，不存檔）。只在 framework 執行緒用；不碰遊戲狀態。
/// 倒地停止時 <see cref="Capture"/>、復活時 <see cref="TakePrompt"/>（同一次倒地只提示一次）、使用者按恢復時 <see cref="Take"/>；
/// 換區、登出、手動開始任何一項、全部停止時 <see cref="Invalidate"/>。不會自己恢復：恢復一律由使用者按下才做。
/// </summary>
public sealed class DeathRestore
{
    private readonly bool[] kinds = new bool[AutomationKindExtensions.All.Length];

    private bool prompted;

    public bool HasSnapshot { get; private set; }

    public bool Contains(AutomationKind kind)
    {
        return HasSnapshot && kinds[(int)kind];
    }

    /// <summary>換成這次倒地前在跑的清單（舊的整份丟掉）；回傳有沒有記下任何一項。</summary>
    /// <param name="running">以 <see cref="AutomationKind"/> 的值為索引。</param>
    public bool Capture(ReadOnlySpan<bool> running)
    {
        Invalidate();
        var count = Math.Min(running.Length, kinds.Length);
        for (var i = 0; i < count; i++)
        {
            kinds[i] = running[i];
            HasSnapshot |= running[i];
        }

        return HasSnapshot;
    }

    /// <summary>復活時呼叫：有清單、而且這份清單還沒提示過才回 true。</summary>
    public bool TakePrompt()
    {
        if (!HasSnapshot || prompted)
        {
            return false;
        }

        prompted = true;
        return true;
    }

    /// <summary>丟掉清單；回傳原本有沒有清單（呼叫端決定要不要寫記錄）。</summary>
    public bool Invalidate()
    {
        var had = HasSnapshot;
        Array.Clear(kinds);
        HasSnapshot = false;
        prompted = false;
        return had;
    }

    /// <summary>取走清單（寫進 <paramref name="into"/>）並清空；沒有清單時回 false、不動 <paramref name="into"/>。</summary>
    public bool Take(Span<bool> into)
    {
        if (!HasSnapshot)
        {
            return false;
        }

        into.Clear();
        kinds.AsSpan(0, Math.Min(kinds.Length, into.Length)).CopyTo(into);
        Invalidate();
        return true;
    }
}
