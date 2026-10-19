using System;
using System.Numerics;
using BOCCHI.Data;
using BOCCHI.Modules.Aggro;
using BOCCHI.Modules.Fates;

namespace BOCCHI.Modules.Automator;

/// <summary>等待結束時的分類（記錄用）。</summary>
public enum PotWaitEnd : byte
{
    /// <summary>魔法甕（任一個）出現在 F.A.T.E. 表上。</summary>
    Appeared,

    /// <summary>推估時間過了寬限仍沒出現：推估作廢（不知道下一次何時出現）。</summary>
    Missed,

    /// <summary>關了設定、魔法甕優先，或推估的那個魔法甕取消勾選。</summary>
    SettingOff,

    /// <summary>推估還在、但已經不在出發窗內（例：改小了分鐘）。</summary>
    NotDue,

    /// <summary>推估沒了（換區、登出）。</summary>
    Unknown,
}

/// <summary>
/// 設定「魔法甕快出現時先到出生點等」的純邏輯：該不該（繼續）等、候選等待點、挑等待點、結束的分類。
/// 不碰遊戲（離線驗收直接呼叫）；時間一律 UTC；距離只看 X/Z。
/// </summary>
public static class PotWaitRules
{
    /// <summary>離出生點不超過這個距離就算到了，開始挑等待點。</summary>
    public const float ArriveM = 25f;

    /// <summary>等待中離出生點超過這個距離（不是自己手動走開的）就重新前往出生點。</summary>
    public const float ReturnM = 40f;

    /// <summary>挑等待點：離每個仇恨範圍至少這麼遠才算安全。</summary>
    public const float ClearanceM = 3f;

    /// <summary>站著等時：離最近的仇恨範圍不到這麼遠才換點（比挑點寬鬆，魔物走動一點不會來回換）。</summary>
    public const float RepickClearanceM = 1f;

    /// <summary>候選點貼到導航網格後，水平偏移超過這個值＝那個方向沒有地面。</summary>
    public const float MaxSnapDriftM = 3f;

    /// <summary>候選點與出生點的高低差上限：另一層地形（台下、崖下）不算出生點附近。</summary>
    public const float MaxHeightDiffM = 6f;

    /// <summary>候選等待點：出生點本身＋半徑 6～30 米五圈、每圈 12 個方向。</summary>
    public const int Rings = 5;

    public const float RingStepM = 6f;

    public const int Directions = 12;

    public const int CandidateCount = 1 + (Rings * Directions);

    // 同一圈的候選點離出生點的距離只差浮點誤差：差不到這個值算同距離（取前面的）。
    private const float TieM = 0.01f;

    /// <summary>出發窗：「不接緊急遭遇戰」與「不接 F.A.T.E.」兩個窗取大的（只算有參加的種類）；0＝都關。</summary>
    public static int DepartureMinutes(AutomatorConfig config)
    {
        var ce = config.ShouldDoCriticalEncounters ? config.SkipCriticalEncountersBeforePotMinutes : 0;
        var fate = config.ShouldDoFates ? config.SkipFatesBeforePotMinutes : 0;
        return Math.Max(Math.Max(ce, fate), 0);
    }

    /// <summary>
    /// 這一刻該不該（繼續）到出生點等：設定與魔法甕優先開著、推估可信（不是「不知道」）、推估的魔法甕有勾選參加、
    /// 在出發窗內（推估時間已過但還在寬限內也算），而且知道那個魔法甕的出生點。
    /// </summary>
    public static bool WantsWait(AutomatorConfig config, PotCycle cycle, TimeSpan period, DateTime nowUtc, out uint potId, out TimeSpan remaining)
    {
        potId = 0;
        remaining = default;
        if (!config.ShouldWaitAtPotSpawn || !config.ShouldPrioritizePots)
        {
            return false;
        }

        if (!PotReminder.IsDueWithin(cycle, nowUtc, period, DepartureMinutes(config), out potId, out remaining)
            || !config.TryGetFateEnabled(potId, out var enabled) || !enabled || SpawnPoint(potId) == null)
        {
            potId = 0;
            remaining = default;
            return false;
        }

        return true;
    }

    /// <summary>等待失效那一刻的分類；<paramref name="potOnTable" />＝任一個魔法甕在 F.A.T.E. 表上。</summary>
    public static PotWaitEnd ClassifyEnd(AutomatorConfig config, PotCycle cycle, TimeSpan period, DateTime nowUtc, uint waitedId, bool potOnTable)
    {
        if (potOnTable)
        {
            return PotWaitEnd.Appeared;
        }

        if (!config.ShouldWaitAtPotSpawn || !config.ShouldPrioritizePots || !config.TryGetFateEnabled(waitedId, out var enabled) || !enabled)
        {
            return PotWaitEnd.SettingOff;
        }

        if (!cycle.TryPredict(nowUtc, period, out _, out _, out var missed))
        {
            return PotWaitEnd.Unknown;
        }

        return missed > 0 ? PotWaitEnd.Missed : PotWaitEnd.NotDue;
    }

    /// <summary>BOCCHI 資料裡的魔法甕 F.A.T.E. 位置（與前往魔法甕時用的同一個）；沒有回 null。</summary>
    public static Vector3? SpawnPoint(uint potId)
    {
        return EventData.Fates.TryGetValue(potId, out var data) ? data.StartPosition : null;
    }

    /// <summary>第 index 個候選（還沒貼到地面，高度沿用出生點）：0＝出生點本身，之後由內圈到外圈。</summary>
    public static Vector3 Candidate(Vector3 spawn, int index)
    {
        if (index <= 0)
        {
            return spawn;
        }

        var ring = ((index - 1) / Directions) + 1;
        var angle = (index - 1) % Directions * (2f * MathF.PI / Directions);
        var radius = ring * RingStepM;
        return new Vector3(spawn.X + (radius * MathF.Cos(angle)), spawn.Y, spawn.Z + (radius * MathF.Sin(angle)));
    }

    /// <summary>貼到導航網格的結果能不能用：有地面、水平偏移與高低差都在上限內。</summary>
    public static bool AcceptSnap(Vector3 raw, Vector3? snapped, Vector3 spawn)
    {
        return snapped is { } p && DistanceXZ(p, raw) <= MaxSnapDriftM && MathF.Abs(p.Y - spawn.Y) <= MaxHeightDiffM;
    }

    /// <summary>
    /// 點到最近一個仇恨範圍的距離（在範圍內＝0）；只算狀態不低於 minState、高度在範圍內的；沒有時回正無限大。
    /// 視覺扇形一律當整圓：站著等好幾分鐘，魔物會轉身。
    /// </summary>
    public static float Clearance(Vector3 point, ReadOnlySpan<AggroZone> zones, AggroState minState)
    {
        var best = float.PositiveInfinity;
        foreach (ref readonly var zone in zones)
        {
            if (zone.State < minState || !AggroZones.InBand(zone, point.Y))
            {
                continue;
            }

            var dx = point.X - zone.Center.X;
            var dz = point.Z - zone.Center.Z;
            var outer = MathF.Max(zone.Radius, zone.BodyRadius);
            best = MathF.Min(best, MathF.Max(MathF.Sqrt((dx * dx) + (dz * dz)) - outer, 0f));
        }

        return best;
    }

    public static bool IsSafe(Vector3 point, ReadOnlySpan<AggroZone> zones, AggroState minState)
    {
        return Clearance(point, zones, minState) >= ClearanceM;
    }

    public static bool NeedsReposition(float clearance)
    {
        return clearance < RepickClearanceM;
    }

    /// <summary>
    /// 從貼好地面的候選（null＝那個方向不能用）挑等待點：安全的裡面離出生點最近的；都不安全時挑離出生點最近的（safe＝false）。
    /// 一個候選都沒有時回 false。同距離取前面的候選（內圈、角度小的先）。
    /// </summary>
    public static bool TryPick(ReadOnlySpan<Vector3?> candidates, Vector3 spawn, ReadOnlySpan<AggroZone> zones, AggroState minState, out Vector3 point, out bool safe)
    {
        point = default;
        safe = false;
        var found = false;
        var bestSafe = float.PositiveInfinity;
        var bestAny = float.PositiveInfinity;
        Vector3 anyPoint = default;
        foreach (var candidate in candidates)
        {
            if (candidate is not { } c)
            {
                continue;
            }

            var d = DistanceXZ(c, spawn);
            if (d < bestAny - TieM)
            {
                bestAny = d;
                anyPoint = c;
                found = true;
            }

            if (d < bestSafe - TieM && IsSafe(c, zones, minState))
            {
                bestSafe = d;
                point = c;
                safe = true;
            }
        }

        if (!safe)
        {
            point = anyPoint;
        }

        return found;
    }

    public static float DistanceXZ(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }
}
