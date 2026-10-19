using System.Collections.Generic;
using System.Numerics;
using BOCCHI.Pathfinding;

namespace BOCCHI.Data;

public static class TreasureData
{
    public readonly record struct TreasureDatum(uint Id, Vector3 Position, uint Type);

    /// <summary>刻意不去的生成點填這個等級（比區域等級上限的最大值 28 高，任何設定都不會排進巡迴）。</summary>
    public const uint Avoided = 99;

    /// <summary>刻意不去的生成點：那裡的寶箱照樣記錄與顯示，但不排進巡迴、不插隊。</summary>
    public static bool IsAvoided(uint node)
    {
        return Levels.TryGetValue(node, out var level) && level == Avoided;
    }

    /// <summary>
    /// 寶箱在網格接不上的低台上、巡迴要先跳上去的生成點（地面 Y 3.0、台面 3.8）。起跳點與落點取自使用者實機騎乘跳上去的那一次。
    /// </summary>
    public readonly static Dictionary<uint, HuntLedge> Ledges = new()
    {
        { 1842, new HuntLedge(new Vector3(-878.9f, 3.0f, -686.5f), new Vector3(-885.5f, 3.8f, -678.0f), 3.5f, 12f) },
    };

    public readonly static Dictionary<uint, uint> Levels = new()
    {
        { 1789, 5 },
        { 1790, 11 },
        { 1791, 13 },
        { 1792, 16 },
        { 1793, 14 },
        { 1794, 23 },
        { 1795, 25 },
        { 1796, 28 },
        { 1797, 1 },
        { 1798, 1 },
        { 1799, 2 },
        { 1800, 2 },
        { 1801, 3 },
        { 1802, 3 },
        { 1803, 4 },
        { 1804, 4 },
        { 1805, 5 },
        { 1806, 5 },
        { 1807, 3 },
        { 1808, 6 },
        { 1809, 6 },
        { 1810, 7 },
        { 1811, 8 },
        { 1812, 8 },
        { 1813, 9 },
        { 1814, 9 },
        { 1815, 10 },
        { 1816, 10 },
        { 1817, 11 },
        { 1818, 11 },
        { 1819, 12 },
        { 1820, 12 },
        { 1821, 13 },
        { 1822, 13 },
        { 1823, 14 },
        { 1824, 14 },
        { 1825, 15 },
        { 1826, 15 },
        { 1827, 16 },
        { 1828, 16 },
        { 1829, 17 },
        { 1830, 17 },
        { 1831, 18 },
        { 1832, 18 },
        { 1833, 19 },
        { 1834, 19 },
        { 1835, 20 },
        { 1836, 20 },
        { 1837, 21 },
        { 1838, 21 },
        { 1839, 22 },
        { 1840, 22 },
        { 1841, 22 },
        { 1842, 23 }, // 寶箱在低台上：網格只有從台上往下的連結，上台要跳（Ledges）
        { 1843, 24 },
        { 1844, 24 },
        { 1845, 25 },
        { 1846, 25 },
        { 1847, 26 },
        { 1848, 26 },
        { 1849, 27 },
        { 1850, 27 },
        { 1851, 28 },
        { 1852, 28 },
        { 1853, 21 },
        { 1854, 10 },
        { 1855, 11 },
        { 1856, 11 },
    };
}
