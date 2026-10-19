using System;
using System.Numerics;
using BOCCHI.Data;

namespace BOCCHI.Modules.Aggro;

// 本檔是趕路迴避的純判斷：不碰遊戲、不配置，離線測試直接呼叫。全部只在框架執行緒用（不加鎖）。

/// <summary>這個區域這一次不避的原因。</summary>
public enum AvoidExempt : byte
{
    None = 0,

    /// <summary>玩家已經在裡面（vnavmesh 也會豁免含起點的區域）。</summary>
    StartInside = 1,

    /// <summary>終點離這個區域不到 <see cref="AvoidanceRules.DestinationExemptM"/> 米：避了也走不到。</summary>
    Destination = 2,

    /// <summary>目前選中的目標：要走過去的對象。</summary>
    Target = 3,

    /// <summary>聽覺範圍、而且走路穿越可用：不繞，接近時切成走路（見 <see cref="WalkThroughRules"/>）。</summary>
    WalkThrough = 4,
}

public enum AvoidVerdict : byte
{
    Accept = 0,
    NoPath = 1,
    EmptyPath = 2,
    ClientPathLinks = 3,
    EndFar = 4,
    StartFar = 5,
    NotFewer = 6,
    TooLong = 7,

    /// <summary>穿越數一樣、新路線也沒有短 <see cref="AvoidanceRules.ShorterGainM"/> 米以上：保持目前的路線。</summary>
    NotShorter = 8,

    /// <summary>避不開、但 vnavmesh 給了穿越較淺的路線（<see cref="ExposureChange.Shallower"/>）：換上。</summary>
    Mitigated = 9,

    /// <summary>目前走的是減傷路線，新路線穿越數一樣但較深：保持目前的路線。</summary>
    Deeper = 10,
}

/// <summary>新路線對目前路線的曝露，只看兩條路線在前方穿過的區域（見 <see cref="AvoidanceRules.CompareExposure"/>）。</summary>
public enum ExposureChange : byte
{
    Same = 0,

    /// <summary>區域內總長少 <see cref="AvoidanceRules.MitigateGainM"/> 米以上，而且每個區域離中心最近的距離都沒有變近。</summary>
    Shallower = 1,

    /// <summary>區域內總長多 <see cref="AvoidanceRules.MitigateGainM"/> 米以上，或有區域離中心變近。</summary>
    Deeper = 2,
}

/// <summary>一條路線在一組區域裡的曝露：區域內總長（米）、區域內離中心最近的 X/Z 距離（沒進入＝+∞）。</summary>
public readonly record struct RouteExposure(float InsideM, float NearestM);

public enum ReplanDecision : byte
{
    Plan = 0,
    Suppressed = 1,
    TooSoon = 2,
    Unchanged = 3,

    /// <summary>這一趟 30 秒內繞不開太多次：保持目前的路線（重算改短暫停、路線新穿過範圍時放慢到每 <see cref="ReplanGate.SlowIntervalMs"/> 一次）；或重算改短已用完這一趟的次數。</summary>
    RateLimited = 4,
}

/// <summary>為什麼要送這一次規劃。</summary>
public enum PlanKind : byte
{
    /// <summary>這一趟第一次發現前方穿過範圍。</summary>
    First = 0,

    /// <summary>目前的路線（可能已是繞路）新穿過範圍：有新的區域，或原本那些移動／轉身了。</summary>
    Crossing = 1,

    /// <summary>正在走繞路、而當初要繞開的區域消失（進入戰鬥、死亡、走遠）、移動或轉身：看看有沒有更短的路。</summary>
    Recheck = 2,
}

/// <summary><see cref="ZoneSignature.Stale"/> 的結果：當初要繞開的區域怎麼了。</summary>
public enum SignatureChange : byte
{
    None = 0,

    /// <summary>不在區域清單上了（魔物進入戰鬥、死亡、離開掃描範圍，或那個感知不再成立）。</summary>
    Gone = 1,

    Moved = 2,

    Turned = 3,
}

public enum StuckState : byte
{
    Idle = 0,
    Watching = 1,
    Progress = 2,
    Stuck = 3,
}

/// <summary>Nav.PathfindAroundZones 回傳的旗標（vnavmesh 端只增不改）。</summary>
public static class VnavZoneFlags
{
    public const int PartialPath = 0x1;

    public const int OverBudget = 0x2;

    public const int IterationLimit = 0x4;

    /// <summary>含客戶端固定路徑連結：不可以直接 Path.MoveTo。</summary>
    public const int ClientPathLinks = 0x8;

    public const int StartExempt = 0x10;

    public const int EndExempt = 0x20;

    public const int ExceptionFallback = 0x40;

    public const int OffMeshLinks = 0x80;

    public const int BadZoneData = 0x100;

    public const int NoPath = 0x200;

    /// <summary>避不開的區域改走穿越較淺的路線：仍穿過（穿越數不變），vnavmesh 量的曝露少 20% 以上。</summary>
    public const int Mitigated = 0x400;
}

public static class AvoidanceRules
{
    public const long CheckIntervalMs = 250;

    /// <summary>只看、也只請 vnavmesh 修補路徑起點算起這麼多米（3D 弧長）內的穿越；與掃描半徑相同。</summary>
    public const float HorizonM = 60f;

    /// <summary>路徑終點離登記的目的地（X/Z）這麼近才算 BOCCHI 自己的趕路；目的地不在網格上時 vnavmesh 會落到旁邊。</summary>
    public const float OwnPathToleranceM = 5f;

    /// <summary>新路徑的終點離原本路徑的終點不能超過這個距離（3D）。</summary>
    public const float ResultEndToleranceM = 3f;

    /// <summary>新路徑的第一點（算路當下的位置）離玩家不能超過這個距離（3D）。</summary>
    public const float StartToleranceM = 5f;

    /// <summary>
    /// 新路線最多比目前的路線多走 max(<see cref="LengthExtraMinM"/>, 目前路長 × <see cref="LengthFraction"/>)，
    /// 與 vnavmesh 的繞路預算同一個式子（舊版 vnavmesh 預算較寬，由這裡擋）；另加 <see cref="LengthToleranceM"/> 吸收兩邊量法的誤差。
    /// </summary>
    public const float LengthFraction = 0.25f;

    public const float LengthExtraMinM = 15f;

    public const float LengthToleranceM = 1f;

    /// <summary>穿越數相同時，新路線至少要短這麼多才換（避免為了幾米來回換路）。</summary>
    public const float ShorterGainM = 5f;

    public const float DestinationExemptM = 5f;

    /// <summary>減傷路線的區域內總長至少要少這麼多才算較淺（多這麼多算較深）。</summary>
    public const float MitigateGainM = 1f;

    /// <summary>離中心最近的距離變近超過這個值才算變近（吸收取樣誤差）。</summary>
    public const float NearToleranceM = 0.25f;

    /// <summary>量區域內長度的取樣間隔。</summary>
    public const float ExposureStepM = 0.25f;

    /// <summary>vnavmesh 一次最多採用的區域筆數。</summary>
    public const int MaxRequestZones = 256;

    /// <summary>照目前速度這麼多秒內會進確定範圍＝緊急（<see cref="ReplanGate"/> 略過最短間隔與預算）。</summary>
    public const float UrgentSeconds = 1.5f;

    /// <summary>緊急的前瞻距離下限（剛出發、速度還量不到時用）。</summary>
    public const float UrgentMinM = 8f;

    public static float UrgentDistance(float speed)
    {
        return Math.Clamp(speed * UrgentSeconds, UrgentMinM, HorizonM);
    }

    /// <summary>
    /// 戰鬥中要不要停下迴避：下了坐騎（原地打，移動交給戰鬥 AI 或使用者）才停；
    /// 騎乘中 BOCCHI 照常趕路，所以照常繞開還沒發現你的魔物（已進入戰鬥的魔物掃描時就不算範圍）。
    /// </summary>
    public static bool PausedByCombat(bool inCombat, bool mounted)
    {
        return inCombat && !mounted;
    }

    /// <summary>
    /// 從 start 沿 path 走 horizon 米內，最先進入的區域（skip 為 0、狀態不低於 minState）與進入點的弧長；沒有回 +∞、index＝−1。
    /// 進入點在碰到的那一段裡二分求得（前段碰到 ⇒ 更長的前段一定碰到）。
    /// </summary>
    public static float FirstCrossingDistance(ReadOnlySpan<AggroZone> zones, ReadOnlySpan<byte> skip, AggroState minState, Vector3 start, ReadOnlySpan<Vector3> path, float horizon, out int index)
    {
        index = -1;
        if (minState == AggroState.None)
        {
            minState = AggroState.Possible;
        }

        var travelled = 0f;
        var previous = start;
        var remaining = horizon > 0f ? horizon : float.PositiveInfinity;
        foreach (var point in path)
        {
            var length = Vector3.Distance(previous, point);
            var last = length >= remaining;
            var end = last && length > 0f ? Vector3.Lerp(previous, point, remaining / length) : point;
            var span = last ? remaining : length;
            var best = float.PositiveInfinity;
            for (var i = 0; i < zones.Length; i++)
            {
                if (skip[i] != 0 || zones[i].State < minState || !AggroZones.SegmentHits(zones[i], previous, end))
                {
                    continue;
                }

                var at = EntryFraction(zones[i], previous, end) * span;
                if (at < best)
                {
                    best = at;
                    index = i;
                }
            }

            if (index >= 0)
            {
                return travelled + best;
            }

            if (last)
            {
                break;
            }

            travelled += length;
            remaining -= length;
            previous = point;
        }

        return float.PositiveInfinity;
    }

    private static float EntryFraction(in AggroZone zone, Vector3 from, Vector3 to)
    {
        if (AggroZones.SegmentHits(zone, from, from))
        {
            return 0f;
        }

        var lo = 0f;
        var hi = 1f;
        for (var k = 0; k < 16; k++)
        {
            var mid = (lo + hi) * 0.5f;
            if (AggroZones.SegmentHits(zone, from, Vector3.Lerp(from, to, mid)))
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        return hi;
    }

    public static AvoidExempt Exempt(in AggroZone zone, Vector3 player, Vector3 destination, ulong targetId)
    {
        if (AggroZones.Contains(zone, player))
        {
            return AvoidExempt.StartInside;
        }

        if (AggroZones.InBand(zone, destination.Y) && AggroZones.Distance2D(zone, destination) <= DestinationExemptM)
        {
            return AvoidExempt.Destination;
        }

        return targetId != 0 && zone.GameObjectId == targetId ? AvoidExempt.Target : AvoidExempt.None;
    }

    /// <summary>walkThrough＝走路穿越可用（設定開、不在戰鬥、按鍵可用；見 <see cref="WalkThroughRules.Gate"/>）：聽覺範圍不繞。原本的四參數版＝walkThrough 為 false。</summary>
    public static AvoidExempt Exempt(in AggroZone zone, Vector3 player, Vector3 destination, ulong targetId, bool walkThrough)
    {
        var reason = Exempt(zone, player, destination, targetId);
        return reason == AvoidExempt.None && walkThrough && WalkThroughRules.IsWalkable(zone) ? AvoidExempt.WalkThrough : reason;
    }

    public static void MarkExempt(ReadOnlySpan<AggroZone> zones, Vector3 player, Vector3 destination, ulong targetId, Span<byte> exempt)
    {
        MarkExempt(zones, player, destination, targetId, false, exempt);
    }

    public static void MarkExempt(ReadOnlySpan<AggroZone> zones, Vector3 player, Vector3 destination, ulong targetId, bool walkThrough, Span<byte> exempt)
    {
        for (var i = 0; i < zones.Length; i++)
        {
            exempt[i] = (byte)Exempt(zones[i], player, destination, targetId, walkThrough);
        }
    }

    public static bool IsOwnPath(Vector3 pathEnd, Vector3 destination)
    {
        var dx = pathEnd.X - destination.X;
        var dz = pathEnd.Z - destination.Z;
        return dx * dx + dz * dz <= OwnPathToleranceM * OwnPathToleranceM;
    }

    /// <summary>目前的路徑是不是 applied 的後段（vnavmesh 只從前面消耗路徑點；被別人換掉就不是）。</summary>
    public static bool IsSuffixOf(ReadOnlySpan<Vector3> current, ReadOnlySpan<Vector3> applied)
    {
        if (current.Length == 0 || current.Length > applied.Length)
        {
            return false;
        }

        return current[^1] == applied[^1] && current[0] == applied[applied.Length - current.Length];
    }

    /// <summary>新路徑開頭那一點是算路當下的位置：離玩家夠近就丟掉，否則 vnavmesh 會先回頭走到那一點。</summary>
    public static ReadOnlySpan<Vector3> TrimStart(ReadOnlySpan<Vector3> path, Vector3 player)
    {
        return path.Length >= 2 && Vector3.Distance(path[0], player) <= StartToleranceM ? path[1..] : path;
    }

    /// <summary>
    /// 新路徑能不能換上。crossings／length 一律從玩家目前位置沿路算（新路徑用 TrimStart 之後的），兩條都在結果回來那一刻量。
    /// 穿越變少：不超過長度上限就換；穿越一樣：要短 <see cref="ShorterGainM"/> 米以上才換；穿越變多：不換。
    /// </summary>
    public static AvoidVerdict Validate(int flags, ReadOnlySpan<Vector3> path, Vector3 player, Vector3 expectedEnd, int newCrossings, int currentCrossings, float newLength, float currentLength)
    {
        return Validate(flags, path, player, expectedEnd, newCrossings, currentCrossings, newLength, currentLength, ExposureChange.Same, false);
    }

    /// <summary>
    /// 帶曝露的版本（8 參數版＝Same、keepShallow false）。穿越一樣（&gt; 0）時：vnavmesh 回報減傷、
    /// 新路線較淺、不超過長度上限 ⇒ <see cref="AvoidVerdict.Mitigated"/>；keepShallow（目前走的是減傷路線）時短 5 米但較深的不換。
    /// </summary>
    public static AvoidVerdict Validate(int flags, ReadOnlySpan<Vector3> path, Vector3 player, Vector3 expectedEnd, int newCrossings, int currentCrossings, float newLength, float currentLength,
        ExposureChange exposure, bool keepShallow)
    {
        if ((flags & VnavZoneFlags.NoPath) != 0)
        {
            return AvoidVerdict.NoPath;
        }

        if (path.Length == 0)
        {
            return AvoidVerdict.EmptyPath;
        }

        if ((flags & VnavZoneFlags.ClientPathLinks) != 0)
        {
            return AvoidVerdict.ClientPathLinks;
        }

        if (Vector3.Distance(path[^1], expectedEnd) > ResultEndToleranceM)
        {
            return AvoidVerdict.EndFar;
        }

        if (Vector3.Distance(path[0], player) > StartToleranceM)
        {
            return AvoidVerdict.StartFar;
        }

        if (newCrossings > currentCrossings)
        {
            return AvoidVerdict.NotFewer;
        }

        if (newCrossings == currentCrossings)
        {
            if (currentCrossings > 0 && (flags & VnavZoneFlags.Mitigated) != 0 && exposure == ExposureChange.Shallower && newLength <= MaxLength(currentLength))
            {
                return AvoidVerdict.Mitigated;
            }

            if (newLength <= currentLength - ShorterGainM)
            {
                return keepShallow && exposure == ExposureChange.Deeper ? AvoidVerdict.Deeper : AvoidVerdict.Accept;
            }

            return currentCrossings > 0 ? AvoidVerdict.NotFewer : AvoidVerdict.NotShorter;
        }

        return newLength > MaxLength(currentLength) ? AvoidVerdict.TooLong : AvoidVerdict.Accept;
    }

    /// <summary>
    /// 比較兩條路線（都從 start 起算、整條）的曝露：只看 currentHit 或 nextHit 標出的區域（CountCrossings 的 hit）。
    /// 較淺＝區域內總長少 <see cref="MitigateGainM"/> 以上且每個區域離中心都沒變近；較深＝總長多 <see cref="MitigateGainM"/> 以上或有區域變近。
    /// </summary>
    public static ExposureChange CompareExposure(ReadOnlySpan<AggroZone> zones, ReadOnlySpan<bool> currentHit, ReadOnlySpan<bool> nextHit, Vector3 start,
        ReadOnlySpan<Vector3> current, ReadOnlySpan<Vector3> next, out RouteExposure currentExposure, out RouteExposure nextExposure)
    {
        float currentInside = 0f, nextInside = 0f;
        float currentNearest = float.PositiveInfinity, nextNearest = float.PositiveInfinity;
        var closer = false;
        for (var i = 0; i < zones.Length; i++)
        {
            if (!currentHit[i] && !nextHit[i])
            {
                continue;
            }

            MeasureZone(zones[i], start, current, out var ci, out var cn);
            MeasureZone(zones[i], start, next, out var ni, out var nn);
            currentInside += ci;
            nextInside += ni;
            currentNearest = MathF.Min(currentNearest, cn);
            nextNearest = MathF.Min(nextNearest, nn);
            closer |= nn < cn - NearToleranceM;
        }

        currentExposure = new RouteExposure(currentInside, currentNearest);
        nextExposure = new RouteExposure(nextInside, nextNearest);
        if (closer || nextInside > currentInside + MitigateGainM)
        {
            return ExposureChange.Deeper;
        }

        return nextInside <= currentInside - MitigateGainM ? ExposureChange.Shallower : ExposureChange.Same;
    }

    // 沿路每 ExposureStepM 取中點：在區域內的長度、區域內離中心最近的 X/Z 距離（沒進入＝+∞）。
    private static void MeasureZone(in AggroZone zone, Vector3 start, ReadOnlySpan<Vector3> path, out float inside, out float nearest)
    {
        inside = 0f;
        nearest = float.PositiveInfinity;
        var previous = start;
        foreach (var point in path)
        {
            var length = Vector3.Distance(previous, point);
            if (length > 0f && AggroZones.SegmentHits(zone, previous, point))
            {
                var steps = Math.Max(1, (int)MathF.Ceiling(length / ExposureStepM));
                for (var k = 0; k < steps; k++)
                {
                    var p = Vector3.Lerp(previous, point, (k + 0.5f) / steps);
                    if (!AggroZones.Contains(zone, p))
                    {
                        continue;
                    }

                    inside += length / steps;
                    var dx = p.X - zone.Center.X;
                    var dz = p.Z - zone.Center.Z;
                    nearest = MathF.Min(nearest, MathF.Sqrt(dx * dx + dz * dz));
                }
            }

            previous = point;
        }
    }

    /// <summary>長度上限：currentLength＋max(LengthExtraMinM, currentLength × LengthFraction)＋LengthToleranceM。</summary>
    public static float MaxLength(float currentLength)
    {
        return currentLength + MathF.Max(LengthExtraMinM, currentLength * LengthFraction) + LengthToleranceM;
    }

    /// <summary>
    /// 這一次檢查要不要看路線：區域要是新的；清單空了但名單上還有要繞開的區域也要看
    /// （那隻去追別人、進入戰鬥就不在清單上，正是要重算改短的時候）。
    /// </summary>
    public static bool ShouldLook(bool zonesFresh, int zoneCount, int signatureCount)
    {
        return zonesFresh && (zoneCount > 0 || signatureCount > 0);
    }

    /// <summary>
    /// 這一次檢查要不要送規劃、算哪一種：目前的路線穿過範圍時，這一趟第一次、或穿過的那組變了（<see cref="ZoneSignature.Changed"/>，含上次已繞開的又被穿過）就重新規劃；
    /// 正在走繞路（onDetour）而當初要繞開的區域消失、移動或轉身（<see cref="ZoneSignature.Stale"/>）就重算看有沒有更短的。
    /// </summary>
    public static bool TryChooseKind(int crossings, bool plannedThisTrip, bool onDetour, ZoneSignature signature, ReadOnlySpan<AggroZone> zones, ReadOnlySpan<bool> hit,
        out PlanKind kind, out SignatureChange change)
    {
        change = SignatureChange.None;
        kind = PlanKind.First;
        if (crossings > 0 && !plannedThisTrip)
        {
            return true;
        }

        if (crossings > 0 && signature.Changed(zones, hit))
        {
            kind = PlanKind.Crossing;
            return true;
        }

        if (onDetour && (change = signature.Stale(zones)) != SignatureChange.None)
        {
            kind = PlanKind.Recheck;
            return true;
        }

        return false;
    }

    /// <summary>第一段（確定＋可能）沒過時，要不要只用確定的範圍再試一次。</summary>
    public static bool ShouldRetryCertainOnly(AggroState firstStage, int certainCrossings)
    {
        return firstStage == AggroState.Possible && certainCrossings > 0;
    }

    /// <summary>
    /// 打包請求：只放沒豁免、狀態不低於 minState 的；有穿越的先放，上限 <see cref="MaxRequestZones"/> 筆，
    /// 剩下的位置補 0（vnavmesh 跳過半徑都是 0 的筆）。回傳放了幾筆。
    /// </summary>
    public static int PackRequest(ReadOnlySpan<AggroZone> zones, ReadOnlySpan<byte> exempt, ReadOnlySpan<bool> hit, AggroState minState, Span<float> destination)
    {
        var capacity = Math.Min(MaxRequestZones, destination.Length / AggroZones.ZoneStride);
        var written = 0;
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < zones.Length && written < capacity; i++)
            {
                if (exempt[i] != 0 || zones[i].State < minState || zones[i].State == AggroState.None || hit[i] != (pass == 0))
                {
                    continue;
                }

                AggroZones.Write(zones[i], destination.Slice(written * AggroZones.ZoneStride, AggroZones.ZoneStride));
                written++;
            }
        }

        destination[(written * AggroZones.ZoneStride)..].Clear();
        return written;
    }
}

/// <summary>
/// 重新規劃的遲滯與頻率上限。時間一律 Environment.TickCount64（毫秒）。
/// 穿越規劃（First／Crossing）與重算改短（Recheck）各算各的：Recheck 不佔穿越的間隔與預算，只排在任何一次規劃的 MinIntervalMs 之後。
/// 預算只計繞不開（<see cref="RecordFailure"/>）與 Recheck，換一趟（<see cref="ResetTrip"/>）清空；成功換上的繞路不計。
/// 緊急（urgent＝很快會進確定範圍）只受 FloorMs 與抑制期限制。卡住退讓（<see cref="Suppress"/>）才是整段不迴避。
/// </summary>
public sealed class ReplanGate
{
    public const long MinIntervalMs = 1500;

    /// <summary>任兩次穿越規劃（含緊急、含換趟後第一次）至少隔這麼久：vnavmesh 呼叫的上界。</summary>
    public const long FloorMs = 500;

    public const long RateWindowMs = 30_000;

    /// <summary>這一趟 30 秒內已經繞不開這麼多次＝重算太頻繁。</summary>
    public const int RateMax = 8;

    /// <summary>這一趟 30 秒內最多送這麼多次重算改短。</summary>
    public const int RecheckMax = 8;

    /// <summary>重算太頻繁時，路線新穿過範圍的重算最多這麼久一次（緊急的不受限）。</summary>
    public const long SlowIntervalMs = 5000;

    private readonly long[] failures = new long[RateMax];

    private readonly long[] rechecks = new long[RecheckMax];

    private int failureCount;

    private int failureNext;

    private int recheckCount;

    private int recheckNext;

    /// <summary>最近一次任何規劃（含 Recheck）。</summary>
    public long LastPlanAt { get; private set; } = long.MinValue / 2;

    /// <summary>最近一次穿越規劃（First／Crossing）。</summary>
    public long LastCrossingAt { get; private set; } = long.MinValue / 2;

    public long SuppressedUntil { get; private set; }

    public bool IsSuppressed(long now)
    {
        return now < SuppressedUntil;
    }

    public bool IsSlow(long now)
    {
        return FailuresWithin(now) >= RateMax;
    }

    public ReplanDecision Evaluate(long now, PlanKind kind)
    {
        return Evaluate(now, kind, false);
    }

    /// <summary>要不要送這一次規劃（呼叫端已判斷有需要）。urgent 只對 First／Crossing 有作用。</summary>
    public ReplanDecision Evaluate(long now, PlanKind kind, bool urgent)
    {
        if (IsSuppressed(now))
        {
            return ReplanDecision.Suppressed;
        }

        if (kind == PlanKind.Recheck)
        {
            if (now - LastPlanAt < MinIntervalMs)
            {
                return ReplanDecision.TooSoon;
            }

            return IsSlow(now) || RechecksWithin(now) >= RecheckMax ? ReplanDecision.RateLimited : ReplanDecision.Plan;
        }

        var sinceCrossing = now - LastCrossingAt;
        if (sinceCrossing < FloorMs)
        {
            return ReplanDecision.TooSoon;
        }

        if (kind == PlanKind.First || urgent)
        {
            return ReplanDecision.Plan;
        }

        if (sinceCrossing < MinIntervalMs)
        {
            return ReplanDecision.TooSoon;
        }

        return IsSlow(now) && sinceCrossing < SlowIntervalMs ? ReplanDecision.RateLimited : ReplanDecision.Plan;
    }

    public void RecordPlan(long now, PlanKind kind)
    {
        LastPlanAt = now;
        if (kind == PlanKind.Recheck)
        {
            Push(rechecks, ref recheckCount, ref recheckNext, now);
            return;
        }

        LastCrossingAt = now;
    }

    /// <summary>穿越規劃的結果是繞不開（照目前的路線走）。</summary>
    public void RecordFailure(long now)
    {
        Push(failures, ref failureCount, ref failureNext, now);
    }

    /// <summary>換一趟：預算清空（上一趟的繞不開與重算不帶進來）；最短間隔與抑制期不清。</summary>
    public void ResetTrip()
    {
        failureCount = failureNext = 0;
        recheckCount = recheckNext = 0;
    }

    public void Suppress(long now, long ms)
    {
        SuppressedUntil = Math.Max(SuppressedUntil, now + ms);
    }

    public int FailuresWithin(long now)
    {
        return Within(failures, failureCount, now);
    }

    public int RechecksWithin(long now)
    {
        return Within(rechecks, recheckCount, now);
    }

    private static void Push(long[] ring, ref int count, ref int next, long now)
    {
        ring[next] = now;
        next = (next + 1) % ring.Length;
        count = Math.Min(count + 1, ring.Length);
    }

    private static int Within(long[] ring, int count, long now)
    {
        var n = 0;
        for (var i = 0; i < count; i++)
        {
            if (now - ring[i] < RateWindowMs)
            {
                n++;
            }
        }

        return n;
    }
}

/// <summary>
/// 這一趟「要繞開的區域」名單：規劃時目前路線穿過的那幾個（<see cref="Merge"/> 累積，記下當時的位置與朝向）。
/// <see cref="Changed"/>＝目前路線穿過的區域裡有名單外的、或名單上的移動超過 3 米／轉身超過 20°（要重新規劃）；
/// <see cref="Stale"/>＝名單上的消失、移動或轉身（正在走繞路時，看看有沒有更短的路）；
/// <see cref="Prune"/>＝已經走過、離前方路線很遠的移出名單。同一隻魔物的不同感知各算一筆。
/// 每個成員另記「已繞開」（<see cref="MarkCleared"/>：上一次換上的路線不穿過它）；已繞開的再被路線穿過也算 <see cref="Changed"/>，
/// 仍穿過的（繞不開、減傷路線）沒移動沒轉身就不重算。
/// </summary>
public sealed class ZoneSignature
{
    public const float MoveM = 3f;

    public const float TurnDegrees = 20f;

    /// <summary>已經走過（離路線最近的地方就是玩家現在的位置）的名單成員，離玩家超過這個距離就移出名單。</summary>
    public const float PassedM = 8f;

    private static readonly float TurnCos = MathF.Cos(TurnDegrees * MathF.PI / 180f);

    private readonly AggroZone[] members;

    private readonly AggroZone[] scratch;

    // 與 members／scratch 同索引。
    private readonly bool[] cleared;

    private readonly bool[] scratchCleared;

    public ZoneSignature(int capacity = AggroZoneSet.Capacity)
    {
        members = new AggroZone[capacity];
        scratch = new AggroZone[capacity];
        cleared = new bool[capacity];
        scratchCleared = new bool[capacity];
    }

    public int Count { get; private set; }

    public void Clear()
    {
        Count = 0;
    }

    /// <summary>名單換成這一組穿越的區域。</summary>
    public void Capture(ReadOnlySpan<AggroZone> zones, ReadOnlySpan<bool> hit)
    {
        Count = 0;
        Merge(zones, hit);
    }

    /// <summary>
    /// 名單上還在的更新成目前的位置與朝向、不在的移出，再加入這一次穿越的。
    /// 這一次穿越的成員清掉「已繞開」（正要為它規劃）；沒穿越的保留。
    /// </summary>
    public void Merge(ReadOnlySpan<AggroZone> zones, ReadOnlySpan<bool> hit)
    {
        var n = 0;
        for (var k = 0; k < Count; k++)
        {
            var i = IndexOf(zones, members[k]);
            if (i >= 0)
            {
                scratchCleared[n] = cleared[k] && !hit[i];
                scratch[n++] = zones[i];
            }
        }

        for (var i = 0; i < zones.Length && n < scratch.Length; i++)
        {
            if (hit[i] && IndexOf(scratch.AsSpan(0, n), zones[i]) < 0)
            {
                scratchCleared[n] = false;
                scratch[n++] = zones[i];
            }
        }

        scratch.AsSpan(0, n).CopyTo(members);
        scratchCleared.AsSpan(0, n).CopyTo(cleared);
        Count = n;
    }

    /// <summary>
    /// 換上新路線後呼叫（stillHit＝新路線在前方穿過的區域）：名單上還在清單裡的成員，沒被穿過的記成已繞開、
    /// 仍被穿過的清掉記號；不在清單上的不動。
    /// </summary>
    public void MarkCleared(ReadOnlySpan<AggroZone> zones, ReadOnlySpan<bool> stillHit)
    {
        for (var k = 0; k < Count; k++)
        {
            var i = IndexOf(zones, members[k]);
            if (i >= 0)
            {
                cleared[k] = !stillHit[i];
            }
        }
    }

    public bool Changed(ReadOnlySpan<AggroZone> zones, ReadOnlySpan<bool> hit)
    {
        for (var i = 0; i < zones.Length; i++)
        {
            if (!hit[i])
            {
                continue;
            }

            var k = IndexOf(members.AsSpan(0, Count), zones[i]);
            if (k < 0 || cleared[k] || Compare(members[k], zones[i]) != SignatureChange.None)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>目前路線穿過的區域裡有沒有「已繞開」的成員（給 log 分辨為什麼重新規劃）。</summary>
    public bool Recrossed(ReadOnlySpan<AggroZone> zones, ReadOnlySpan<bool> hit)
    {
        for (var i = 0; i < zones.Length; i++)
        {
            if (!hit[i])
            {
                continue;
            }

            var k = IndexOf(members.AsSpan(0, Count), zones[i]);
            if (k >= 0 && cleared[k])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>名單上第一個消失、移動或轉身的；都沒變回 None。</summary>
    public SignatureChange Stale(ReadOnlySpan<AggroZone> zones)
    {
        for (var k = 0; k < Count; k++)
        {
            var i = IndexOf(zones, members[k]);
            var change = i < 0 ? SignatureChange.Gone : Compare(members[k], zones[i]);
            if (change != SignatureChange.None)
            {
                return change;
            }
        }

        return SignatureChange.None;
    }

    /// <summary>
    /// 從 start 沿 path 走 horizon 米（≤ 0＝整條）的路線：已經走過的名單成員離玩家超過 PassedM 就移出。
    /// 「走過」＝路線上離它最近的就是玩家現在的位置，而且它在玩家往終點方向的背後（繞路先往反方向走時不算）。
    /// 還在前方的不論多遠都留著。用記錄當時的位置量，魔物自己走遠交給 <see cref="Stale"/>。
    /// </summary>
    public void Prune(Vector3 start, ReadOnlySpan<Vector3> path, float horizon)
    {
        var n = 0;
        var toEnd = path.Length > 0 ? new Vector2(path[^1].X - start.X, path[^1].Z - start.Z) : Vector2.Zero;
        for (var k = 0; k < Count; k++)
        {
            var distance = DistanceToPath(members[k], start, path, horizon);
            var offset = new Vector2(members[k].Center.X - start.X, members[k].Center.Z - start.Z);
            var fromStart = offset.Length() - members[k].Radius;
            var passed = fromStart <= distance + 0.01f && Vector2.Dot(offset, toEnd) < 0f;
            if (!passed || distance <= PassedM)
            {
                cleared[n] = cleared[k];
                members[n++] = members[k];
            }
        }

        Count = n;
    }

    // X/Z 平面上中心到路線的距離減外半徑（扇形當整圓，偏向保留）。
    private static float DistanceToPath(in AggroZone zone, Vector3 start, ReadOnlySpan<Vector3> path, float horizon)
    {
        var c = new Vector2(zone.Center.X, zone.Center.Z);
        var best = Vector2.Distance(c, new Vector2(start.X, start.Z)) - zone.Radius;
        var previous = start;
        var remaining = horizon > 0f ? horizon : float.PositiveInfinity;
        foreach (var point in path)
        {
            var length = Vector3.Distance(previous, point);
            var end = length > remaining && length > 0f ? Vector3.Lerp(previous, point, remaining / length) : point;
            var a = new Vector2(previous.X, previous.Z);
            var ab = new Vector2(end.X, end.Z) - a;
            var len2 = Vector2.Dot(ab, ab);
            var t = len2 > 0f ? Math.Clamp(Vector2.Dot(c - a, ab) / len2, 0f, 1f) : 0f;
            best = MathF.Min(best, Vector2.Distance(c, a + t * ab) - zone.Radius);
            if (length >= remaining)
            {
                break;
            }

            remaining -= length;
            previous = point;
        }

        return best;
    }

    private static int IndexOf(ReadOnlySpan<AggroZone> zones, in AggroZone zone)
    {
        for (var i = 0; i < zones.Length; i++)
        {
            if (zones[i].GameObjectId == zone.GameObjectId && zones[i].Sense == zone.Sense)
            {
                return i;
            }
        }

        return -1;
    }

    private static SignatureChange Compare(in AggroZone before, in AggroZone now)
    {
        var dx = now.Center.X - before.Center.X;
        var dz = now.Center.Z - before.Center.Z;
        if (dx * dx + dz * dz > MoveM * MoveM)
        {
            return SignatureChange.Moved;
        }

        if (now.IsCircle || before.IsCircle)
        {
            return SignatureChange.None;
        }

        return before.DirX * now.DirX + before.DirZ * now.DirZ >= TurnCos ? SignatureChange.None : SignatureChange.Turned;
    }
}

/// <summary>換上繞路之後的卡住偵測：WindowMs 內（只看 X/Z）移動不到 MinMoveM 就算卡住；暫停期間（施法、互動、上坐騎）不計時。</summary>
public sealed class StuckWatch
{
    public const long WindowMs = 3000;

    public const float MinMoveM = 1.5f;

    private Vector3 samplePosition;

    private long sampleAt;

    public bool Active { get; private set; }

    public void Start(long now, Vector3 position)
    {
        Active = true;
        samplePosition = position;
        sampleAt = now;
    }

    public void Stop()
    {
        Active = false;
    }

    public StuckState Update(long now, Vector3 position, bool paused)
    {
        if (!Active)
        {
            return StuckState.Idle;
        }

        var dx = position.X - samplePosition.X;
        var dz = position.Z - samplePosition.Z;
        if (paused || dx * dx + dz * dz >= MinMoveM * MinMoveM)
        {
            samplePosition = position;
            sampleAt = now;
            return paused ? StuckState.Watching : StuckState.Progress;
        }

        if (now - sampleAt < WindowMs)
        {
            return StuckState.Watching;
        }

        Active = false;
        return StuckState.Stuck;
    }
}
