using System;
using BOCCHI.Enums;

namespace BOCCHI.Modules.Treasure;

/// <summary>
/// 魔尋寶感知到的寶箱數（整個新月島區域）與感知之後開掉的數量：某一種開掉的＋已記錄還沒開的達到感知數＝那一種的位置都知道了，
/// 巡迴可以跳過那一種、又沒有已記錄寶箱的生成點（白銀生成點只出白銀寶箱，青銅同理）。
/// 開箱數由呼叫端給寶箱記錄的累計數（<see cref="TreasureHuntMemory.OpenedCount"/>），已記錄數給
/// <see cref="TreasureHuntMemory.CountChests"/>。純邏輯、不碰遊戲，只在框架執行緒用。
/// </summary>
public enum TreasureSenseShow
{
    /// <summary>沒感知過（或登出後）：畫成「?」。</summary>
    Unknown = 0,

    /// <summary>感知數還有效：實色。</summary>
    Fresh,

    /// <summary>作廢的舊數字：淡色，標明是上次的。</summary>
    Stale,
}

/// <summary>主視窗「魔尋寶感知」那一列要畫的內容：Bronze／Silver＝現在還有幾個（感知數扣掉之後開掉的），Events＝感知後參與了幾場。</summary>
public readonly record struct TreasureSenseView(TreasureSenseShow State, int Bronze, int Silver, int Events)
{
    /// <summary>有效而且到上限：再等也不會多。</summary>
    public bool BronzeFull
    {
        get => State == TreasureSenseShow.Fresh && Bronze >= TreasureSenseCount.BronzeCap;
    }

    public bool SilverFull
    {
        get => State == TreasureSenseShow.Fresh && Silver >= TreasureSenseCount.SilverCap;
    }
}

public sealed class TreasureSenseCount
{
    /// <summary>整個區域同時存在的青銅／白銀寶箱上限（社群資料；實機 12 輪沒超過）：只用在顯示。</summary>
    public const int BronzeCap = 30;

    public const int SilverCap = 8;

    private int silverBase;

    private int bronzeBase;

    public bool Known { get; private set; }

    public int SensedSilver { get; private set; }

    public int SensedBronze { get; private set; }

    /// <summary>收到感知的時間（呼叫端的毫秒數）；<see cref="HasNumbers"/> 為 false 時不看。</summary>
    public long SensedAt { get; private set; }

    /// <summary>每次感知或作廢就加一：給記錄判斷「同一份數字」有沒有寫過。</summary>
    public int Generation { get; private set; }

    /// <summary>有數字可以顯示（感知過、還沒 <see cref="Forget"/>）；<see cref="Known"/> 為 false 時是作廢的舊數字。</summary>
    public bool HasNumbers { get; private set; }

    /// <summary>最近一次感知之後參與了幾場 F.A.T.E.／緊急遭遇戰（顯示舊數字時用）。</summary>
    public int EventsSince { get; private set; }

    /// <summary>
    /// 新的感知取代舊的：數的是「這一刻還在的寶箱」，之前開掉的不在裡面，所以開箱數從這一刻的累計數重新算。
    /// </summary>
    public void Sense(int silver, int bronze, long now, int openedSilverSoFar, int openedBronzeSoFar)
    {
        Known = true;
        SensedSilver = Math.Max(0, silver);
        SensedBronze = Math.Max(0, bronze);
        silverBase = openedSilverSoFar;
        bronzeBase = openedBronzeSoFar;
        SensedAt = now;
        Generation++;
        HasNumbers = true;
        EventsSince = 0;
    }

    /// <summary>收到感知、但這次的數字不採用（參與中、記錄比感知多）：只留著顯示，一開始就是作廢的。</summary>
    public void SenseUnused(int silver, int bronze, long now, int openedSilverSoFar, int openedBronzeSoFar, int eventsSince)
    {
        Sense(silver, bronze, now, openedSilverSoFar, openedBronzeSoFar);
        Known = false;
        EventsSince = Math.Max(0, eventsSince);
    }

    /// <summary>參與了新的一場 F.A.T.E.／緊急遭遇戰。</summary>
    public void NoteEvent()
    {
        if (HasNumbers)
        {
            EventsSince++;
        }
    }

    /// <summary>登出：連顯示用的舊數字一起丟（下次登入的可能是別的角色）。</summary>
    public void Forget()
    {
        Known = false;
        HasNumbers = false;
        EventsSince = 0;
        Generation++;
    }

    /// <summary>
    /// 顯示用：感知數扣掉之後開掉的（不小於 0）。作廢後照樣算，State 是 <see cref="TreasureSenseShow.Stale"/>；沒有數字回預設值（Unknown）。
    /// </summary>
    public TreasureSenseView View(int openedSilverSoFar, int openedBronzeSoFar)
    {
        if (!HasNumbers)
        {
            return default;
        }

        return new TreasureSenseView(
            Known ? TreasureSenseShow.Fresh : TreasureSenseShow.Stale,
            Math.Max(0, SensedBronze - Math.Max(0, openedBronzeSoFar - bronzeBase)),
            Math.Max(0, SensedSilver - Math.Max(0, openedSilverSoFar - silverBase)),
            EventsSince);
    }

    /// <returns>true＝原本有數字、這次作廢。</returns>
    public bool Invalidate()
    {
        if (!Known)
        {
            return false;
        }

        Known = false;
        Generation++;
        return true;
    }

    public int Sensed(TreasureType type)
    {
        return type switch
        {
            TreasureType.Silver => SensedSilver,
            TreasureType.Bronze => SensedBronze,
            _ => 0,
        };
    }

    /// <returns>感知之後開了幾個這種寶箱；-1＝不知道（沒有感知數或不是銀／銅）。</returns>
    public int OpenedSince(TreasureType type, int openedSoFar)
    {
        if (!Known)
        {
            return -1;
        }

        return type switch
        {
            TreasureType.Silver => Math.Max(0, openedSoFar - silverBase),
            TreasureType.Bronze => Math.Max(0, openedSoFar - bronzeBase),
            _ => -1,
        };
    }

    /// <summary>這一種已經開滿感知數（感知到 0 個也算）。</summary>
    public bool IsExhausted(TreasureType type, int openedSoFar)
    {
        return IsExhausted(type, openedSoFar, 0);
    }

    /// <summary>這一種的位置都知道了：感知後開掉的＋已記錄還沒開的（<paramref name="recorded"/>）達到感知數。</summary>
    public bool IsExhausted(TreasureType type, int openedSoFar, int recorded)
    {
        var opened = OpenedSince(type, openedSoFar);
        return opened >= 0 && opened + Math.Max(0, recorded) >= Sensed(type);
    }

    /// <summary>開的比感知的多：感知之後又長出寶箱，數字不能再用。</summary>
    public bool IsOverrun(TreasureType type, int openedSoFar)
    {
        return IsOverrun(type, openedSoFar, 0);
    }

    /// <summary>開掉的＋已記錄還沒開的比感知的多：同樣是感知之後又長出寶箱（或記錄過期），數字不能再用。</summary>
    public bool IsOverrun(TreasureType type, int openedSoFar, int recorded)
    {
        var opened = OpenedSince(type, openedSoFar);
        return opened >= 0 && opened + Math.Max(0, recorded) > Sensed(type);
    }

    /// <summary>巡迴可以跳過這種生成點：那一種開滿了，而且那裡沒有已記錄的寶箱（有就照樣去開）。</summary>
    public bool Skips(TreasureType pointType, bool hasChest, int openedSoFar)
    {
        return Skips(pointType, hasChest, openedSoFar, 0);
    }

    /// <summary>同上，已記錄還沒開的同種寶箱（<paramref name="recorded"/>）也算進去：位置都知道了，其餘的點不會再有。</summary>
    public bool Skips(TreasureType pointType, bool hasChest, int openedSoFar, int recorded)
    {
        return !hasChest && IsExhausted(pointType, openedSoFar, recorded);
    }
}
