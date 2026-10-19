using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using BOCCHI.Data;

namespace BOCCHI.Modules.Aggro;

// 本檔假設與契約：
// 區域是快照的值複本（不含 IGameObject 或原生指標），框架執行緒建、框架執行緒讀。
// 內外判定與 vnavmesh Nav.PathfindAroundZones 相同：與中心高低差 ≤ ZoneHalfHeight，且 X/Z 平面上在區域內。
// 扇形中心方向＝遊戲朝向 (sin θ, cos θ)，與 AggroRenderer 的扇形相同。
// Radius＝外半徑（感知距離＋模型半徑＋邊際）；BodyRadius＝「模型半徑＋邊際」，不論朝向都要避開。

/// <summary>一個迴避區域：外半徑內、且在扇形角度內（整圓時不看角度），或在 BodyRadius 內。</summary>
public struct AggroZone
{
    public Vector3 Center;

    public float Radius;

    /// <summary>扇形中心方向的單位向量；整圓時為 0。</summary>
    public float DirX;

    public float DirZ;

    /// <summary>半角的餘弦；≤ −1＝整圓。</summary>
    public float CosHalfAngle;

    public float BodyRadius;

    public AggroState State;

    /// <summary>產生這個區域的感知類型（單一位元）。</summary>
    public AggroSense Sense;

    public ulong GameObjectId;

    public readonly bool IsCircle
    {
        get => CosHalfAngle <= -1f;
    }
}

/// <summary>每次建區域時從設定抄一份（半徑單位：米，不含模型半徑與邊際；SightHalfAngle 為弧度、不含額外角度）。</summary>
public readonly record struct AggroZoneSettings(
    float SightRadius,
    float SightHalfAngle,
    float SoundRadius,
    float ProximityRadius,
    float MagicRadius,
    float BloodRadius,
    float UnknownRadius,
    float Margin,
    bool Mounted);

/// <summary>純函式：不碰遊戲、不配置，離線測試直接呼叫。</summary>
public static class AggroZones
{
    /// <summary>打包給 vnavmesh 時每個區域佔幾個 float：[x, y, z, radius, dirX, dirZ, cosHalfAngle, bodyRadius]。</summary>
    public const int ZoneStride = 8;

    /// <summary>整圓的 CosHalfAngle：比 −1 小，對「≤ −1」與「dot ≥ cos」兩種判法都成立。</summary>
    public const float FullCircleCos = -2f;

    /// <summary>視覺扇形每邊多避的角度：朝向在掃描間隔內會轉動，角度本身也是估計值。</summary>
    public const float SightExtraHalfAngle = 10f * MathF.PI / 180f;

    /// <summary>AggroSense 共 6 個位元：每隻怪最多 6 個區域。</summary>
    public const int MaxZonesPerEntry = 6;

    /// <summary>點與區域中心的高低差超過這個值就不算在區域內（另一層地形）；與 vnavmesh 的 ZoneHalfHeight 相同。</summary>
    public const float ZoneHalfHeight = 10f;

    /// <summary>把快照轉成區域，寫進 output，回傳寫了幾個；output 滿了就停。</summary>
    public static int Build(ReadOnlySpan<AggroEntry> entries, in AggroZoneSettings settings, Span<AggroZone> output)
    {
        var count = 0;
        var margin = MathF.Max(settings.Margin, 0f);
        var halfAngle = MathF.Max(settings.SightHalfAngle, 0f) + SightExtraHalfAngle;
        var sightCos = halfAngle >= MathF.PI ? FullCircleCos : MathF.Cos(halfAngle);

        foreach (ref readonly var entry in entries)
        {
            if (entry.State == AggroState.None)
            {
                continue;
            }

            var body = MathF.Max(entry.HitboxRadius, 0f) + margin;
            var senses = entry.Senses;

            if ((senses & AggroSense.Sight) != 0)
            {
                if (count >= output.Length)
                {
                    return count;
                }

                var (dirX, dirZ) = sightCos <= -1f ? (0f, 0f) : (MathF.Sin(entry.Rotation), MathF.Cos(entry.Rotation));
                output[count++] = Make(entry, AggroSense.Sight, settings.SightRadius + body, dirX, dirZ, sightCos, body);
            }

            if ((senses & AggroSense.Sound) != 0 && !Circle(entry, AggroSense.Sound, settings.SoundRadius, body, output, ref count))
            {
                return count;
            }

            if ((senses & AggroSense.Proximity) != 0 && !Circle(entry, AggroSense.Proximity, settings.ProximityRadius, body, output, ref count))
            {
                return count;
            }

            // 騎乘中不能詠唱魔法，魔法感知的怪不會發現你。
            if ((senses & AggroSense.Magic) != 0 && !settings.Mounted && !Circle(entry, AggroSense.Magic, settings.MagicRadius, body, output, ref count))
            {
                return count;
            }

            if ((senses & AggroSense.Blood) != 0 && entry.BloodActive && !Circle(entry, AggroSense.Blood, settings.BloodRadius, body, output, ref count))
            {
                return count;
            }

            if ((senses & AggroSense.Unknown) != 0 && !Circle(entry, AggroSense.Unknown, settings.UnknownRadius, body, output, ref count))
            {
                return count;
            }
        }

        return count;
    }

    /// <summary>「迴避可能」關閉時只避確定。</summary>
    public static AggroState MinState(bool avoidPossible)
    {
        return avoidPossible ? AggroState.Possible : AggroState.Certain;
    }

    /// <summary>
    /// 把狀態不低於 minState 的區域依序打包成每筆 <see cref="ZoneStride"/> 個 float，回傳打包了幾筆；
    /// destination 放不下的就不打包（呼叫端用回傳值判斷有沒有截斷）。格式見 <see cref="Write"/>。
    /// </summary>
    public static int Pack(ReadOnlySpan<AggroZone> zones, AggroState minState, Span<float> destination)
    {
        if (minState == AggroState.None)
        {
            minState = AggroState.Possible;
        }

        var written = 0;
        foreach (ref readonly var zone in zones)
        {
            if (zone.State < minState)
            {
                continue;
            }

            var offset = written * ZoneStride;
            if (offset + ZoneStride > destination.Length)
            {
                break;
            }

            Write(zone, destination.Slice(offset, ZoneStride));
            written++;
        }

        return written;
    }

    /// <summary>
    /// 一筆 vnavmesh 格式：[x, y, z, radius, dirX, dirZ, cosHalfAngle, bodyRadius]。
    /// vnavmesh 的外圈＝radius＋bodyRadius，所以 radius 寫「外半徑−BodyRadius」（不含模型半徑與邊際）。
    /// </summary>
    public static void Write(in AggroZone zone, Span<float> destination)
    {
        destination[0] = zone.Center.X;
        destination[1] = zone.Center.Y;
        destination[2] = zone.Center.Z;
        destination[3] = MathF.Max(zone.Radius - zone.BodyRadius, 0f);
        destination[4] = zone.DirX;
        destination[5] = zone.DirZ;
        destination[6] = zone.CosHalfAngle;
        destination[7] = zone.BodyRadius;
    }

    public static bool InBand(in AggroZone zone, float y)
    {
        return MathF.Abs(y - zone.Center.Y) <= ZoneHalfHeight;
    }

    /// <summary>點是否在區域內（邊界算在內；高低差超過 <see cref="ZoneHalfHeight"/> 不算）。</summary>
    public static bool Contains(in AggroZone zone, Vector3 point)
    {
        return InBand(zone, point.Y) && ContainsFlat(zone, point);
    }

    /// <summary>X/Z 平面上點到區域的距離，在區域內＝0；不看高度。</summary>
    public static float Distance2D(in AggroZone zone, Vector3 point)
    {
        var dx = point.X - zone.Center.X;
        var dz = point.Z - zone.Center.Z;
        var r = MathF.Sqrt(dx * dx + dz * dz);
        var toBody = MathF.Max(r - zone.BodyRadius, 0f);
        if (zone.IsCircle || r < 1e-6f || InWedge(zone, dx, dz, r))
        {
            return MathF.Min(toBody, MathF.Max(r - zone.Radius, 0f));
        }

        var (edgeA, edgeB) = Edges(zone);
        var p = new Vector2(dx, dz);
        var toEdges = MathF.Sqrt(MathF.Min(DistanceSquaredToSegment(p, Vector2.Zero, edgeA), DistanceSquaredToSegment(p, Vector2.Zero, edgeB)));
        return MathF.Min(toBody, toEdges);
    }

    private static bool ContainsFlat(in AggroZone zone, Vector3 point)
    {
        var dx = point.X - zone.Center.X;
        var dz = point.Z - zone.Center.Z;
        var d2 = dx * dx + dz * dz;
        if (d2 <= zone.BodyRadius * zone.BodyRadius)
        {
            return true;
        }

        if (d2 > zone.Radius * zone.Radius)
        {
            return false;
        }

        return zone.IsCircle || InWedge(zone, dx, dz, MathF.Sqrt(d2));
    }

    /// <summary>線段 from→to 是否碰到區域（含兩端點、擦邊算碰到）；只看與中心高低差 ≤ ZoneHalfHeight 的那一段。</summary>
    public static bool SegmentHits(in AggroZone zone, Vector3 from, Vector3 to)
    {
        return ClipToBand(zone.Center.Y, ref from, ref to) && SegmentHitsFlat(zone, from, to);
    }

    private static bool SegmentHitsFlat(in AggroZone zone, Vector3 from, Vector3 to)
    {
        var c = new Vector2(zone.Center.X, zone.Center.Z);
        var a = new Vector2(from.X, from.Z);
        var b = new Vector2(to.X, to.Z);
        var nearest = DistanceSquaredToSegment(c, a, b);
        if (nearest <= zone.BodyRadius * zone.BodyRadius)
        {
            return true;
        }

        var r = zone.Radius;
        if (nearest > r * r)
        {
            return false;
        }

        if (zone.IsCircle || ContainsFlat(zone, from) || ContainsFlat(zone, to))
        {
            return true;
        }

        // 兩端都在扇形外卻穿過扇形 ⇒ 一定穿過它的邊界：兩條半徑或那段圓弧。
        var (edgeA, edgeB) = Edges(zone);
        if (SegmentsIntersect(a, b, c, c + edgeA) || SegmentsIntersect(a, b, c, c + edgeB))
        {
            return true;
        }

        return CrossesArc(zone, c, a, b, r);
    }

    /// <summary>從 start 沿 path 走的總長（3D）。</summary>
    public static float PathLength(Vector3 start, ReadOnlySpan<Vector3> path)
    {
        var length = 0f;
        var previous = start;
        foreach (var point in path)
        {
            length += Vector3.Distance(previous, point);
            previous = point;
        }

        return length;
    }

    /// <summary>
    /// 從 start 沿 path 走 horizon 米（3D 弧長；≤ 0＝整條）之內碰到幾個區域：只算 skip 為 0、狀態不低於 minState 的；
    /// hit 標出是哪幾個（長度至少與 zones 相同）。
    /// </summary>
    public static int CountCrossings(ReadOnlySpan<AggroZone> zones, ReadOnlySpan<byte> skip, AggroState minState, Vector3 start, ReadOnlySpan<Vector3> path, float horizon, Span<bool> hit)
    {
        if (minState == AggroState.None)
        {
            minState = AggroState.Possible;
        }

        hit[..zones.Length].Clear();
        var count = 0;
        var previous = start;
        var remaining = horizon > 0f ? horizon : float.PositiveInfinity;
        foreach (var point in path)
        {
            var end = point;
            var length = Vector3.Distance(previous, point);
            var last = length >= remaining;
            if (last && length > 0f)
            {
                end = Vector3.Lerp(previous, point, remaining / length);
            }

            for (var i = 0; i < zones.Length; i++)
            {
                if (!hit[i] && skip[i] == 0 && zones[i].State >= minState && SegmentHits(zones[i], previous, end))
                {
                    hit[i] = true;
                    count++;
                }
            }

            if (last)
            {
                break;
            }

            remaining -= length;
            previous = point;
        }

        return count;
    }

    public static int CountCrossings(ReadOnlySpan<AggroZone> zones, ReadOnlySpan<byte> skip, AggroState minState, Vector3 start, List<Vector3> path, float horizon, Span<bool> hit)
    {
        return CountCrossings(zones, skip, minState, start, CollectionsMarshal.AsSpan(path), horizon, hit);
    }

    // 兩條半徑（從中心起算、長度＝外半徑）的向量。
    private static (Vector2 A, Vector2 B) Edges(in AggroZone zone)
    {
        var cos = Math.Clamp(zone.CosHalfAngle, -1f, 1f);
        var sin = MathF.Sqrt(MathF.Max(0f, 1f - cos * cos));
        var dir = new Vector2(zone.DirX, zone.DirZ);
        var r = zone.Radius;
        return (r * new Vector2(dir.X * cos - dir.Y * sin, dir.X * sin + dir.Y * cos), r * new Vector2(dir.X * cos + dir.Y * sin, -dir.X * sin + dir.Y * cos));
    }

    // 把線段裁成與中心高低差 ≤ ZoneHalfHeight 的那一段；整段都在外回 false。
    private static bool ClipToBand(float centerY, ref Vector3 a, ref Vector3 b)
    {
        var low = centerY - ZoneHalfHeight;
        var high = centerY + ZoneHalfHeight;
        var dy = b.Y - a.Y;
        if (MathF.Abs(dy) < 1e-6f)
        {
            return a.Y >= low && a.Y <= high;
        }

        var t0 = (low - a.Y) / dy;
        var t1 = (high - a.Y) / dy;
        if (t0 > t1)
        {
            (t0, t1) = (t1, t0);
        }

        t0 = MathF.Max(t0, 0f);
        t1 = MathF.Min(t1, 1f);
        if (t0 > t1)
        {
            return false;
        }

        var start = a;
        a = Vector3.Lerp(start, b, t0);
        b = Vector3.Lerp(start, b, t1);
        return true;
    }

    /// <summary>第一個被線段碰到、且狀態不低於 minState 的區域索引；沒有回 −1。</summary>
    public static int FirstHit(ReadOnlySpan<AggroZone> zones, AggroState minState, Vector3 from, Vector3 to)
    {
        if (minState == AggroState.None)
        {
            minState = AggroState.Possible;
        }

        for (var i = 0; i < zones.Length; i++)
        {
            if (zones[i].State >= minState && SegmentHits(zones[i], from, to))
            {
                return i;
            }
        }

        return -1;
    }

    private static AggroZone Make(in AggroEntry entry, AggroSense sense, float radius, float dirX, float dirZ, float cosHalfAngle, float body)
    {
        return new AggroZone
        {
            Center = entry.Position,
            Radius = MathF.Max(radius, body),
            DirX = dirX,
            DirZ = dirZ,
            CosHalfAngle = cosHalfAngle,
            BodyRadius = body,
            State = entry.State,
            Sense = sense,
            GameObjectId = entry.GameObjectId,
        };
    }

    private static bool Circle(in AggroEntry entry, AggroSense sense, float senseRadius, float body, Span<AggroZone> output, ref int count)
    {
        if (count >= output.Length)
        {
            return false;
        }

        output[count++] = Make(entry, sense, senseRadius + body, 0f, 0f, FullCircleCos, body);
        return true;
    }

    // dot(d, dir) ≥ cos·|d|：|d|＝0（正好在中心）也算在內。
    private static bool InWedge(in AggroZone zone, float dx, float dz, float length)
    {
        return dx * zone.DirX + dz * zone.DirZ >= zone.CosHalfAngle * length;
    }

    private static bool CrossesArc(in AggroZone zone, Vector2 c, Vector2 a, Vector2 b, float r)
    {
        var d = b - a;
        var f = a - c;
        var qa = Vector2.Dot(d, d);
        if (qa <= 0f)
        {
            return false;
        }

        var qb = 2f * Vector2.Dot(f, d);
        var qc = Vector2.Dot(f, f) - r * r;
        var disc = qb * qb - 4f * qa * qc;
        if (disc < 0f)
        {
            return false;
        }

        var root = MathF.Sqrt(disc);
        return ArcPointInWedge(zone, f, d, (-qb - root) / (2f * qa), r) || ArcPointInWedge(zone, f, d, (-qb + root) / (2f * qa), r);
    }

    private static bool ArcPointInWedge(in AggroZone zone, Vector2 f, Vector2 d, float t, float r)
    {
        if (t < 0f || t > 1f)
        {
            return false;
        }

        var p = f + t * d;
        return InWedge(zone, p.X, p.Y, r);
    }

    private static float DistanceSquaredToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var len2 = Vector2.Dot(ab, ab);
        var t = len2 > 0f ? Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
        return Vector2.DistanceSquared(p, a + t * ab);
    }

    private static float Cross(Vector2 o, Vector2 a, Vector2 b)
    {
        return (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
    }

    private static bool OnSegment(Vector2 p, Vector2 q, Vector2 r)
    {
        return MathF.Min(p.X, q.X) <= r.X && r.X <= MathF.Max(p.X, q.X) && MathF.Min(p.Y, q.Y) <= r.Y && r.Y <= MathF.Max(p.Y, q.Y);
    }

    private static bool SegmentsIntersect(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2)
    {
        var d1 = Cross(q1, q2, p1);
        var d2 = Cross(q1, q2, p2);
        var d3 = Cross(p1, p2, q1);
        var d4 = Cross(p1, p2, q2);
        if (((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f)) && ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f)))
        {
            return true;
        }

        return (d1 == 0f && OnSegment(q1, q2, p1))
               || (d2 == 0f && OnSegment(q1, q2, p2))
               || (d3 == 0f && OnSegment(p1, p2, q1))
               || (d4 == 0f && OnSegment(p1, p2, q2));
    }
}

/// <summary>
/// 區域的可重用固定容量緩衝區：AggroModule.Update 建、同一條執行緒讀。
/// 容量＝快照容量×每隻怪最多的區域數，所以建區域永遠不會截斷。
/// </summary>
public sealed class AggroZoneSet
{
    public const int Capacity = AggroSnapshot.Capacity * AggroZones.MaxZonesPerEntry;

    private readonly AggroZone[] zones = new AggroZone[Capacity];

    public int Count { get; private set; }

    /// <summary>最後一次建區域的 Environment.TickCount64；0＝目前沒有（迴避沒在用或剛清掉）。</summary>
    public long BuiltAt { get; private set; }

    public ReadOnlySpan<AggroZone> Zones
    {
        get => zones.AsSpan(0, Count);
    }

    public void Build(ReadOnlySpan<AggroEntry> entries, in AggroZoneSettings settings, long now)
    {
        Count = AggroZones.Build(entries, settings, zones);
        BuiltAt = now;
    }

    public void Clear()
    {
        Count = 0;
        BuiltAt = 0;
    }

    public bool IsFresh(long now, long maxAgeMs)
    {
        return BuiltAt != 0 && now - BuiltAt <= maxAgeMs;
    }
}
