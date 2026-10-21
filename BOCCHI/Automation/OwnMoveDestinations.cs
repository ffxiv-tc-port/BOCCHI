using System;
using System.Numerics;

namespace BOCCHI.Automation;

// 本檔契約：只在框架執行緒呼叫，不加鎖、不配置。
// vnavmesh 不記路線是誰送的，能看到的只有 Path.ListWaypoints 的終點，所以「是不是 BOCCHI 送的」用終點對 BOCCHI 自己送過的目的地判斷。
// 趕路（TravelIntent）以外直接送 PathfindAndMoveTo 的地方（追敵人、自動打怪）要呼叫 Note。

/// <summary>停止後的導航守衛對 vnavmesh 正在走的路線怎麼處理。</summary>
public enum OwnPathVerdict : byte
{
    /// <summary>vnavmesh 沒在走。</summary>
    Idle,

    /// <summary>終點是 BOCCHI 送過的目的地：再停一次。</summary>
    Own,

    /// <summary>終點離 BOCCHI 送過的目的地都太遠：別的插件或使用者送的，不停。</summary>
    Foreign,

    /// <summary>讀不到終點：照舊再停一次。</summary>
    Unknown,
}

/// <summary>BOCCHI 送給 vnavmesh 的最近幾個目的地（固定容量的環）。</summary>
public sealed class OwnMoveDestinations
{
    public const int Capacity = 8;

    /// <summary>
    /// 路線終點離目的地（只看 X/Z）不超過這個距離就算同一個：vnavmesh 會把走不到的終點換成同一區最近的點，
    /// 實機 BOCCHI 趕路的終點偏差最大 2.7 米，追到懸崖上的敵人偏差 18.5 米。
    /// </summary>
    public const float MatchRadiusM = 20f;

    /// <summary>全插件共用：沒有登記成趕路的移動（追敵人、自動打怪）。</summary>
    public static readonly OwnMoveDestinations Recent = new();

    private readonly Vector3[] points = new Vector3[Capacity];

    private int next;

    public int Count { get; private set; }

    public static void Note(Vector3 destination)
    {
        Recent.Add(destination);
    }

    public void Add(Vector3 destination)
    {
        points[next] = destination;
        next = (next + 1) % Capacity;
        if (Count < Capacity)
        {
            Count++;
        }
    }

    public void Clear()
    {
        Count = 0;
        next = 0;
    }

    public void CopyFrom(OwnMoveDestinations other)
    {
        Array.Copy(other.points, points, Capacity);
        next = other.next;
        Count = other.Count;
    }

    /// <summary>離最近一個目的地的水平距離；沒有任何目的地時回正無限大。</summary>
    public float NearestXZ(Vector3 position)
    {
        var best = float.PositiveInfinity;
        for (var i = 0; i < Count; i++)
        {
            var dx = points[i].X - position.X;
            var dz = points[i].Z - position.Z;
            best = MathF.Min(best, MathF.Sqrt(dx * dx + dz * dz));
        }

        return best;
    }

    /// <param name="running">vnavmesh Path.IsRunning。</param>
    /// <param name="pathEnd">Path.ListWaypoints 的最後一點；讀不到或是空清單時是 null。</param>
    public OwnPathVerdict Judge(bool running, Vector3? pathEnd)
    {
        if (!running)
        {
            return OwnPathVerdict.Idle;
        }

        if (pathEnd is not { } end)
        {
            return OwnPathVerdict.Unknown;
        }

        return NearestXZ(end) <= MatchRadiusM ? OwnPathVerdict.Own : OwnPathVerdict.Foreign;
    }

    /// <summary>等同 <c>CaptureCurrent(intent)</c> 之後 <see cref="Count"/> &gt; 0，但不改任何實例。</summary>
    public static bool HasCurrent(TravelIntentState intent)
    {
        return Recent.Count > 0 || (intent.HasIntent && intent.Current.Source != TravelSource.EurekaPotHunt);
    }

    /// <summary>換成「現在算 BOCCHI 自己的」目的地：最近送出的，加上還沒作廢的趕路登記（替 EurekaHelper 登記的不算：那是它的路，停下導航時不可以再停它）。</summary>
    public void CaptureCurrent(TravelIntentState intent)
    {
        CopyFrom(Recent);
        if (intent.HasIntent && intent.Current.Source != TravelSource.EurekaPotHunt)
        {
            Add(intent.Current.Destination);
        }
    }
}
