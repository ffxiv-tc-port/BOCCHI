using System;
using System.Numerics;

namespace BOCCHI.Automation;

// 本檔契約：純邏輯，不碰遊戲、不配置（離線測試直接呼叫）。只存 id 與時間戳；時間一律 Environment.TickCount64 的毫秒。
// 候選清單的順序＝離玩家由近到遠（TargetHelper.Enemies 的順序）；「最近」與「重心」平手時取清單裡較前面的。

public enum TargetPickMode : byte
{
    Closest = 0,

    Centroid = 1,
}

public enum TargetVerdict : byte
{
    /// <summary>維持秒數 0：照舊每次改寫成清單挑出的那一隻，清單空時清空。</summary>
    Legacy = 0,

    /// <summary>清單是空的：不寫。</summary>
    NoCandidates = 1,

    /// <summary>目前目標是活著、可選的活動敵人。</summary>
    KeepActivityEnemy = 2,

    /// <summary>目前目標正在打自己。</summary>
    KeepSelfDefense = 3,

    /// <summary>目前目標不是 BOCCHI 上次寫的、而且合法：維持秒數內不動。</summary>
    KeepForeign = 4,

    Switch = 5,
}

/// <param name="DistanceToPlayer">與排序同一種量法（Player.DistanceTo）。</param>
public readonly record struct TargetCandidate(ulong Id, Vector3 Position, float DistanceToPlayer);

/// <param name="Id">0＝沒有目標。</param>
/// <param name="Legal">敵對、活著、可選。</param>
/// <param name="TargetingPlayer">它的目標是玩家自己。</param>
public readonly record struct CurrentTarget(ulong Id, bool Legal, bool TargetingPlayer);

/// <summary>跨次呼叫要記的狀態：BOCCHI 上次寫的 id、目前看到的「別人選的」id 與第一次看到的時間。</summary>
public struct TargetHoldState
{
    public ulong LastWrittenId;

    public ulong ForeignId;

    public long ForeignSince;
}

/// <param name="TargetId">要寫的 id（0＝清空，只有 Legacy 會）；不寫的判斷是目前目標的 id。</param>
/// <param name="LegacyId">舊邏輯這一次會寫的 id（統計「遲滯保留」用）。</param>
public readonly record struct TargetDecision(TargetVerdict Verdict, ulong TargetId, ulong LegacyId)
{
    public bool Writes
    {
        get => Verdict is TargetVerdict.Legacy or TargetVerdict.Switch;
    }
}

public static class TargetHysteresis
{
    /// <summary>真的要換時，原本的目標比最佳的遠不超過這麼多（米）就留原本的。</summary>
    public const float TieToleranceM = 3f;

    /// <param name="holdMs">「目標最短維持秒數」換成毫秒；≤ 0＝舊行為。</param>
    public static TargetDecision Decide(ReadOnlySpan<TargetCandidate> candidates, TargetPickMode mode, in CurrentTarget current, ref TargetHoldState state, long holdMs, long now)
    {
        var legacyIndex = Pick(candidates, mode, 0, 0f);
        var legacyId = legacyIndex < 0 ? 0 : candidates[legacyIndex].Id;
        if (holdMs <= 0)
        {
            state.LastWrittenId = legacyId;
            state.ForeignId = 0;
            return new TargetDecision(TargetVerdict.Legacy, legacyId, legacyId);
        }

        if (candidates.IsEmpty)
        {
            return new TargetDecision(TargetVerdict.NoCandidates, current.Id, legacyId);
        }

        if (current.Id != 0 && IndexOf(candidates, current.Id) >= 0)
        {
            return new TargetDecision(TargetVerdict.KeepActivityEnemy, current.Id, legacyId);
        }

        if (current.Id != 0 && current.Legal && current.TargetingPlayer)
        {
            return new TargetDecision(TargetVerdict.KeepSelfDefense, current.Id, legacyId);
        }

        if (current.Id != 0 && current.Legal && current.Id != state.LastWrittenId)
        {
            if (state.ForeignId != current.Id)
            {
                state.ForeignId = current.Id;
                state.ForeignSince = now;
            }

            if (now - state.ForeignSince < holdMs)
            {
                return new TargetDecision(TargetVerdict.KeepForeign, current.Id, legacyId);
            }
        }

        var index = Pick(candidates, mode, state.LastWrittenId, TieToleranceM);
        var id = candidates[index].Id;
        state.LastWrittenId = id;
        state.ForeignId = 0;
        return new TargetDecision(TargetVerdict.Switch, id, legacyId);
    }

    /// <summary>
    /// 挑一隻：最近＝離玩家最近、重心＝離所有候選平均位置最近；平手取前面的。
    /// preferredId 在清單裡、而且分數不比最佳差超過 tolerance 時改挑它。清單空回 −1。
    /// </summary>
    public static int Pick(ReadOnlySpan<TargetCandidate> candidates, TargetPickMode mode, ulong preferredId, float tolerance)
    {
        if (candidates.IsEmpty)
        {
            return -1;
        }

        var centroid = Vector3.Zero;
        if (mode == TargetPickMode.Centroid)
        {
            foreach (ref readonly var candidate in candidates)
            {
                centroid += candidate.Position;
            }

            centroid /= candidates.Length;
        }

        var best = 0;
        var bestScore = Score(candidates[0], mode, centroid);
        for (var i = 1; i < candidates.Length; i++)
        {
            var score = Score(candidates[i], mode, centroid);
            if (score < bestScore)
            {
                best = i;
                bestScore = score;
            }
        }

        if (preferredId == 0 || candidates[best].Id == preferredId)
        {
            return best;
        }

        var preferred = IndexOf(candidates, preferredId);
        if (preferred < 0)
        {
            return best;
        }

        // 重心模式比的是平方距離（與舊版排序一致），容差換回米再比。
        var preferredScore = Score(candidates[preferred], mode, centroid);
        var bestM = mode == TargetPickMode.Centroid ? MathF.Sqrt(bestScore) : bestScore;
        var preferredM = mode == TargetPickMode.Centroid ? MathF.Sqrt(preferredScore) : preferredScore;
        return preferredM <= bestM + tolerance ? preferred : best;
    }

    public static int IndexOf(ReadOnlySpan<TargetCandidate> candidates, ulong id)
    {
        for (var i = 0; i < candidates.Length; i++)
        {
            if (candidates[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private static float Score(in TargetCandidate candidate, TargetPickMode mode, Vector3 centroid)
    {
        return mode == TargetPickMode.Centroid ? Vector3.DistanceSquared(candidate.Position, centroid) : candidate.DistanceToPlayer;
    }
}
