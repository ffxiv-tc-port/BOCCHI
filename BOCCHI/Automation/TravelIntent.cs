using System;
using System.Numerics;
using ECommons.DalamudServices;

namespace BOCCHI.Automation;

// 本檔契約：只在框架執行緒（遊戲主執行緒；動作鏈、巡迴步驟、UI 按鈕都在這裡）呼叫，不加鎖。
// 只有「趕路」登記；戰鬥中、追 F.A.T.E. 目標、自動打怪的移動刻意不登記：不登記＝不套迴避。

/// <summary>是誰送出這趟趕路（給 log 與迴避用）。</summary>
public enum TravelSource : byte
{
    None = 0,

    IllegalModeFate = 1,

    IllegalModeCriticalEncounter = 2,

    /// <summary>非法模式先走到最近的城內乙太之晶，再傳送。</summary>
    IllegalModeShard = 3,

    /// <summary>傳送面板的「走過去」。</summary>
    TeleporterWalk = 4,

    /// <summary>傳送面板的「傳送後走過去」。</summary>
    TeleporterAfterTeleport = 5,

    /// <summary>傳送前走到最近的城內乙太之晶。</summary>
    NearestShard = 6,

    /// <summary>返回後走到乙太之光旁。</summary>
    Aetheryte = 7,

    /// <summary>返回後走到 KnowledgeCrystal 旁補增益效果。</summary>
    KnowledgeCrystal = 8,

    HuntNode = 9,

    HuntShard = 10,

    /// <summary>緊急遭遇戰報名範圍內的隨機站位。</summary>
    CriticalEncounterSpot = 11,

    /// <summary>非法模式兩個活動之間順路去開白銀寶箱。</summary>
    SilverChest = 12,

    /// <summary>非法模式魔法甕快出現時，走到出生點附近的等待點。</summary>
    PotWaitSpot = 13,

    /// <summary>EurekaHelper 魔法甕尋寶自動走向下的路線：只為了套仇恨迴避而登記，不算 BOCCHI 自己送的路（見 OwnMoveDestinations.CaptureCurrent）。</summary>
    EurekaPotHunt = 14,
}

/// <param name="Generation">登記當下的停止世代號（每次 AutomationControl 停下導航 +1）。</param>
/// <param name="Sequence">登記序號（每次新的趕路 +1；同一趟重下不變）。</param>
public readonly record struct TravelIntentSnapshot(Vector3 Destination, TravelSource Source, ushort Territory, long RegisteredAt, int Generation, int Sequence);

/// <summary>最近一趟趕路的登記。純狀態，時間與區域由呼叫端給，離線測試直接用。</summary>
public sealed class TravelIntentState
{
    /// <summary>離目的地（只看 X/Z）不超過這個距離就當作已經到了。</summary>
    public const float ArriveRadius = 3f;

    /// <summary>登記超過這麼久就當作已經結束（沒走到也不再算趕路）。</summary>
    public const long MaxAgeMs = 10 * 60 * 1000;

    // 同一趟重下（例：巡迴在 vnavmesh 還沒開走時每 150 ms 重下一次）目的地的容許差。
    private const float SameDestination = 0.5f;

    public bool HasIntent { get; private set; }

    public int Generation { get; private set; }

    public int Sequence { get; private set; }

    public TravelIntentSnapshot Current { get; private set; }

    /// <summary>回傳 true＝新的一趟；false＝同一趟重下，沿用原本的登記。</summary>
    public bool Register(Vector3 destination, TravelSource source, ushort territory, long now)
    {
        var current = Current;
        if (HasIntent
            && current.Source == source
            && current.Territory == territory
            && now - current.RegisteredAt <= MaxAgeMs
            && Vector3.DistanceSquared(current.Destination, destination) <= SameDestination * SameDestination)
        {
            return false;
        }

        Sequence++;
        Current = new TravelIntentSnapshot(destination, source, territory, now, Generation, Sequence);
        HasIntent = true;
        return true;
    }

    /// <summary>回傳 true＝原本有一趟被作廢。</summary>
    public bool Invalidate()
    {
        Generation++;
        var had = HasIntent;
        HasIntent = false;
        return had;
    }

    /// <summary>還在趕路：有登記、同一個區域、沒過期、還沒走到。</summary>
    public bool TryGetActive(ushort territory, Vector3 playerPosition, long now, out TravelIntentSnapshot intent)
    {
        intent = Current;
        if (!HasIntent || intent.Territory != territory || now - intent.RegisteredAt > MaxAgeMs)
        {
            return false;
        }

        var dx = playerPosition.X - intent.Destination.X;
        var dz = playerPosition.Z - intent.Destination.Z;
        return dx * dx + dz * dz > ArriveRadius * ArriveRadius;
    }

    /// <summary>拿到這份登記之後，有沒有停止過或換過一趟（非同步算完路徑後、要動之前用）。</summary>
    public bool IsCurrent(in TravelIntentSnapshot intent)
    {
        return HasIntent && intent.Generation == Generation && intent.Sequence == Sequence;
    }
}

/// <summary>全插件共用的趕路登記（框架執行緒專用）。</summary>
public static class TravelIntent
{
    public static readonly TravelIntentState State = new();

    public static void Register(Vector3 destination, TravelSource source)
    {
        if (State.Register(destination, source, Svc.ClientState.TerritoryType, Environment.TickCount64))
        {
            Svc.Log.Debug($"[BOCCHI][趕路] 登記第 {State.Sequence} 趟：{source} → {destination}");
        }
    }

    public static void Invalidate()
    {
        if (State.Invalidate())
        {
            Svc.Log.Debug($"[BOCCHI][趕路] 停止導航，作廢第 {State.Current.Sequence} 趟（{State.Current.Source}）");
        }
    }
}
