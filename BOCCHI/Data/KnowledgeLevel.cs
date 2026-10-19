using System;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace BOCCHI.Data;

/// <summary>新月島知見等級。Real＝真實等級、Synced＝同步後等級；Real 為 0＝讀不到（不知道）。</summary>
public readonly record struct KnowledgeLevelReading(int Real, int Synced)
{
    public bool IsAvailable
    {
        get => Real > 0;
    }
}

/// <summary>
/// 讀 PlayerState 的內容鍵值欄位（鍵 7＝真實、鍵 6＝同步後）；只讀欄位，不呼叫原生函式。框架執行緒專用。
/// </summary>
public static class KnowledgeLevel
{
    public const uint SyncedKey = 6;

    public const uint RealKey = 7;

    public const int MaxLevel = 50;

    /// <summary>
    /// 南征之章（7.25）的知見等級上限：真實等級 ≥ 這個值就是滿級。EXD 沒有這個值（MKDGrowData 列到 50），
    /// 是執行檔寫死的（新月島內容導演回報的等級上限，經驗值條到這一級顯示 --/--）；改版調高時要跟著改。
    /// </summary>
    public const int SouthHornCap = 20;

    public static KnowledgeLevelReading Read()
    {
        Span<uint> pairs = stackalloc uint[6];
        return TryReadRaw(pairs) ? Interpret(pairs) : default;
    }

    /// <summary>把三組鍵值依序寫進 pairs（鍵、值、鍵、值…）；PlayerState 取不到或還沒載入就回 false。</summary>
    public static unsafe bool TryReadRaw(Span<uint> pairs)
    {
        var state = PlayerState.Instance();
        if (state == null || !state->IsLoaded)
        {
            return false;
        }

        var data = state->ContentKeyValueData;
        var count = Math.Min(data.Length, pairs.Length / 2);
        for (var i = 0; i < count; i++)
        {
            pairs[i * 2] = data[i].Item1;
            pairs[i * 2 + 1] = data[i].Item2;
        }

        return true;
    }

    /// <summary>兩個鍵都找到、都在 1..50、而且同步後 ≤ 真實才算可用；否則回 default。</summary>
    public static KnowledgeLevelReading Interpret(ReadOnlySpan<uint> pairs)
    {
        uint real = 0;
        uint synced = 0;
        var foundReal = false;
        var foundSynced = false;
        for (var i = 0; i + 1 < pairs.Length; i += 2)
        {
            if (pairs[i] == RealKey && !foundReal)
            {
                real = pairs[i + 1];
                foundReal = true;
            }
            else if (pairs[i] == SyncedKey && !foundSynced)
            {
                synced = pairs[i + 1];
                foundSynced = true;
            }
        }

        if (!foundReal || !foundSynced || real is < 1 or > MaxLevel || synced is < 1 or > MaxLevel || synced > real)
        {
            return default;
        }

        return new KnowledgeLevelReading((int)real, (int)synced);
    }
}

/// <summary>每次 Update 讀一次，連續穩定數幀才採用並寫一行 Information（避免切換區域時的閃動洗 log）。</summary>
public sealed class KnowledgeLevelTracker
{
    public const int StableFrames = 10;

    private KnowledgeLevelReading pending;

    private int pendingFrames;

    private bool committedOnce;

    public KnowledgeLevelReading Current { get; private set; }

    /// <returns>true＝這一幀採用的真實等級比上一個採用的高（升級）；見 <see cref="IsRealLevelUp"/>。</returns>
    public bool Update()
    {
        Span<uint> pairs = stackalloc uint[6];
        var loaded = KnowledgeLevel.TryReadRaw(pairs);
        var next = loaded ? KnowledgeLevel.Interpret(pairs) : default;

        if (committedOnce && next == Current)
        {
            pendingFrames = 0;
            return false;
        }

        if (next != pending)
        {
            pending = next;
            pendingFrames = 0;
        }

        if (++pendingFrames < StableFrames)
        {
            return false;
        }

        var raised = IsRealLevelUp(Current, next, committedOnce);
        Log(Current, next, committedOnce, loaded, pairs);
        Current = next;
        committedOnce = true;
        pendingFrames = 0;
        return raised;
    }

    /// <summary>
    /// 兩次採用的讀數之間真實等級變高才算升級；兩邊都要讀得到。第一次採用、讀不到之後再讀到（換區、進島）都不算。純函式。
    /// </summary>
    public static bool IsRealLevelUp(KnowledgeLevelReading before, KnowledgeLevelReading after, bool hadBefore)
    {
        return hadBefore && before.IsAvailable && after.IsAvailable && after.Real > before.Real;
    }

    private static void Log(KnowledgeLevelReading before, KnowledgeLevelReading after, bool hadBefore, bool loaded, ReadOnlySpan<uint> pairs)
    {
        if (after.IsAvailable)
        {
            var change = hadBefore && before.IsAvailable ? $"（原本 真實 {before.Real}、同步後 {before.Synced}）" : string.Empty;
            Svc.Log.Information($"[BOCCHI][知見等級] 讀得到：真實 {after.Real}、同步後 {after.Synced}{change}");
            return;
        }

        Svc.Log.Information(
            $"[BOCCHI][知見等級] 讀不到（PlayerState {(loaded ? "已載入" : "未載入")}，內容鍵值 {pairs[0]}={pairs[1]}、{pairs[2]}={pairs[3]}、{pairs[4]}={pairs[5]}）："
            + "仇恨顯示一律當「可能」，區域等級上限沿用設定值");
    }
}
