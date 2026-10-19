using System;

namespace BOCCHI.Data;

[Flags]
public enum AggroSense : byte
{
    None = 0,

    Sight = 1 << 0,

    Sound = 1 << 1,

    Proximity = 1 << 2,

    Magic = 1 << 3,

    Blood = 1 << 4,

    /// <summary>表上沒有感知類型：一律當「可能」畫預設圓。</summary>
    Unknown = 1 << 5,
}

/// <summary>
/// 一種怪（以 BNpcName id 為鍵）的仇恨資料。
/// <para>Senses 為 None＝刻意不畫；FixedLevel＝只出現一種等級時的等級，多等級填 0（改讀物件上的等級）。</para>
/// <para>LevelMask 第 n 位元＝表上允許等級 n；讀到的等級不在裡面就當讀不到。WeatherId＝只在該天氣出現（0＝不限）。</para>
/// </summary>
public readonly record struct AggroInfo(AggroSense Senses, byte FixedLevel, ulong LevelMask, bool SpawnNight, ushort WeatherId)
{
    public bool AllowsLevel(int level)
    {
        return level is >= 1 and <= 63 && (LevelMask & (1UL << level)) != 0;
    }
}
