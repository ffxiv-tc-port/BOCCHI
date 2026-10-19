using System;
using System.Collections.Generic;
using BOCCHI.Enums;

namespace BOCCHI.Data;

/// <summary>
/// 寶箱巡迴的分段路線：每個生成點屬於哪一段，一段以出發的乙太之晶命名。改分法只改 <see cref="Nodes"/>（與
/// <see cref="TreasureData.Levels"/> 同一組點）；不在表裡的點只在全部路線都巡時排。
/// </summary>
public static class TreasureRoutes
{
    /// <summary>主視窗列出的順序（從調查隊營地順時針）；設定檔存的是 <see cref="Aethernet"/> 的數值。</summary>
    public static readonly Aethernet[] Order =
    [
        Aethernet.BaseCamp,
        Aethernet.Eldergrowth,
        Aethernet.Stonemarsh,
        Aethernet.CrystallizedCaverns,
        Aethernet.TheWanderersHaven,
    ];

    // 先照「從哪個乙太之晶走過去最近」分，再把古樹濕原多出來的點分給調查隊營地與石塔水沼，讓各段一輪的估計時間接近；1853 留在古樹濕原（使用者指定）。
    public static readonly Dictionary<uint, Aethernet> Nodes = new()
    {
        { 1789, Aethernet.BaseCamp },
        { 1790, Aethernet.Stonemarsh },
        { 1791, Aethernet.CrystallizedCaverns },
        { 1792, Aethernet.Eldergrowth },
        { 1793, Aethernet.Eldergrowth },
        { 1794, Aethernet.TheWanderersHaven },
        { 1795, Aethernet.CrystallizedCaverns },
        { 1796, Aethernet.Stonemarsh },
        { 1797, Aethernet.BaseCamp },
        { 1798, Aethernet.BaseCamp },
        { 1799, Aethernet.BaseCamp },
        { 1800, Aethernet.BaseCamp },
        { 1801, Aethernet.BaseCamp },
        { 1802, Aethernet.BaseCamp },
        { 1803, Aethernet.BaseCamp },
        { 1804, Aethernet.Eldergrowth },
        { 1805, Aethernet.Eldergrowth },
        { 1806, Aethernet.BaseCamp },
        { 1807, Aethernet.TheWanderersHaven },
        { 1808, Aethernet.TheWanderersHaven },
        { 1809, Aethernet.TheWanderersHaven },
        { 1810, Aethernet.TheWanderersHaven },
        { 1811, Aethernet.TheWanderersHaven },
        { 1812, Aethernet.TheWanderersHaven },
        { 1813, Aethernet.Eldergrowth },
        { 1814, Aethernet.CrystallizedCaverns },
        { 1815, Aethernet.Stonemarsh },
        { 1816, Aethernet.Eldergrowth },
        { 1817, Aethernet.CrystallizedCaverns },
        { 1818, Aethernet.CrystallizedCaverns },
        { 1819, Aethernet.CrystallizedCaverns },
        { 1820, Aethernet.CrystallizedCaverns },
        { 1821, Aethernet.CrystallizedCaverns },
        { 1822, Aethernet.CrystallizedCaverns },
        { 1823, Aethernet.Eldergrowth },
        { 1824, Aethernet.Eldergrowth },
        { 1825, Aethernet.Eldergrowth },
        { 1826, Aethernet.Eldergrowth },
        { 1827, Aethernet.Eldergrowth },
        { 1828, Aethernet.Eldergrowth },
        { 1829, Aethernet.Eldergrowth },
        { 1830, Aethernet.Eldergrowth },
        { 1831, Aethernet.Stonemarsh },
        { 1832, Aethernet.Stonemarsh },
        { 1833, Aethernet.Stonemarsh },
        { 1834, Aethernet.Stonemarsh },
        { 1835, Aethernet.Stonemarsh },
        { 1836, Aethernet.BaseCamp },
        { 1837, Aethernet.BaseCamp },
        { 1838, Aethernet.BaseCamp },
        { 1839, Aethernet.TheWanderersHaven },
        { 1840, Aethernet.TheWanderersHaven },
        { 1841, Aethernet.TheWanderersHaven },
        { 1842, Aethernet.TheWanderersHaven },
        { 1843, Aethernet.CrystallizedCaverns },
        { 1844, Aethernet.CrystallizedCaverns },
        { 1845, Aethernet.CrystallizedCaverns },
        { 1846, Aethernet.CrystallizedCaverns },
        { 1847, Aethernet.Stonemarsh },
        { 1848, Aethernet.Stonemarsh },
        { 1849, Aethernet.Stonemarsh },
        { 1850, Aethernet.Stonemarsh },
        { 1851, Aethernet.Stonemarsh },
        { 1852, Aethernet.Stonemarsh },
        { 1853, Aethernet.Eldergrowth },
        { 1854, Aethernet.Stonemarsh },
        { 1855, Aethernet.Stonemarsh },
        { 1856, Aethernet.TheWanderersHaven },
    };

    public static int IndexOf(Aethernet route)
    {
        return Array.IndexOf(Order, route);
    }

    /// <summary>這一段在區域等級上限 <paramref name="maxLevel"/> 以內、巡迴會去的點（不含刻意不去的），依 id 排。</summary>
    public static List<uint> NodesOf(Aethernet route, int maxLevel)
    {
        var nodes = new List<uint>();
        foreach (var (node, owner) in Nodes)
        {
            if (owner == route && TreasureData.Levels.TryGetValue(node, out var level) && level <= maxLevel && level != TreasureData.Avoided)
            {
                nodes.Add(node);
            }
        }

        nodes.Sort();
        return nodes;
    }
}
