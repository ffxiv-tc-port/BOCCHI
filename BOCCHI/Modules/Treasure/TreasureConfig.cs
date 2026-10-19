using System.Collections.Generic;
using BOCCHI.Enums;
using BOCCHI.Pathfinding;
using Ocelot.Config.Attributes;
using Ocelot.Modules;

namespace BOCCHI.Modules.Treasure;

public class TreasureConfig : ModuleConfig
{
    [Checkbox]
    [Label("generic.label.enabled")]
    public bool Enabled { get; set; } = true;

    [Checkbox]
    [DependsOn(nameof(Enabled))]
    public bool DrawLineToBronzeChests { get; set; } = true;

    public bool ShouldDrawLineToBronzeChests
    {
        get => IsPropertyEnabled(nameof(DrawLineToBronzeChests));
    }

    [Checkbox]
    [DependsOn(nameof(Enabled))]
    public bool DrawLineToSilverChests { get; set; } = true;

    public bool ShouldDrawLineToSilverChests
    {
        get => IsPropertyEnabled(nameof(DrawLineToSilverChests));
    }

    [Checkbox]
    [DependsOn(nameof(Enabled))]
    public bool AlertSilverChests { get; set; } = true;

    public bool ShouldAlertSilverChests
    {
        get => IsPropertyEnabled(nameof(AlertSilverChests));
    }

    /// <summary>提示音編號，1～16 與聊天的 se.1～se.16 相同。</summary>
    [IntRange(1, 16)]
    [DependsOn(nameof(Enabled), nameof(AlertSilverChests))]
    [Indent]
    public int SilverChestAlertSound { get; set; } = 1;

    [Checkbox]
    [Experimental]
    [Illegal]
    [RequiredPlugin("vnavmesh", "Lifestream")]
    [DependsOn(nameof(Enabled))]
    [Label("modules.treasure.config.show_hunt_button.label")]
    public bool EnableTreasureHunt { get; set; } = false;

    public bool ShouldEnableTreasureHunt
    {
        get => IsPropertyEnabled(nameof(EnableTreasureHunt));
    }

    /// <summary>設定值到這裡（含）＝已記錄的寶箱一律先開，不比較。</summary>
    public const int ChestFirstAlways = 600;

    /// <summary>規劃時已記錄的寶箱全部排在最前面、比跟生成點排成同一趟多花的時間（秒）不超過這個值才先開；0＝只有不會更慢時才先開。</summary>
    [IntRange(0, ChestFirstAlways)]
    [DependsOn(nameof(Enabled), nameof(EnableTreasureHunt))]
    public int HuntChestFirstMaxSeconds { get; set; } = 30;

    /// <summary>寶箱巡迴走路途中看到幸運胡蘿蔔就先去挖（見 <see cref="CarrotDetour"/>）。</summary>
    [Checkbox]
    [Experimental]
    [DependsOn(nameof(Enabled), nameof(EnableTreasureHunt))]
    public bool HuntDigCarrots { get; set; } = false;

    public bool ShouldHuntDigCarrots
    {
        get => IsPropertyEnabled(nameof(HuntDigCarrots));
    }

    /// <summary>幸運胡蘿蔔離玩家（直線，米）不超過這個值才插隊去挖。</summary>
    [IntRange(10, 200)]
    [DependsOn(nameof(Enabled), nameof(EnableTreasureHunt), nameof(HuntDigCarrots))]
    [Indent]
    public int HuntCarrotMaxDistance { get; set; } = 160;

    /// <summary>魔尋寶感知到的某一種寶箱都開完了，巡迴就跳過那一種剩下的生成點（見 <see cref="TreasureSenseCount"/>）。</summary>
    [Checkbox]
    [DependsOn(nameof(Enabled), nameof(EnableTreasureHunt))]
    public bool HuntSkipBySenseCount { get; set; } = true;

    /// <summary>寶箱巡迴開始／續跑時沒有能用的感知數，就先換成輔助自由人補放一次魔尋寶再規劃（見 <see cref="HuntSenseCast"/>）。</summary>
    [Checkbox]
    [DependsOn(nameof(Enabled), nameof(EnableTreasureHunt), nameof(HuntSkipBySenseCount))]
    [Indent]
    public bool HuntCastTreasureSightIfUnknown { get; set; } = true;

    [Checkbox] public bool CastTreasureSightUponReturn { get; set; } = false;

    /// <summary>上次確認施放魔尋寶後這麼多分鐘內返回就不換職、不施放（寶箱要打怪或參加 F.A.T.E.／緊急遭遇戰後才會刷新）；0＝每次返回都施放。</summary>
    [IntRange(0, 120)]
    [DependsOn(nameof(CastTreasureSightUponReturn))]
    [Indent]
    public int TreasureSightIntervalMinutes { get; set; } = 30;

    /// <summary>寶箱巡迴的走法；在主視窗寶箱巡迴那一區選，從下一次按開始生效（續跑沿用那一輪的方向）。</summary>
    public HuntDirection HuntDirection { get; set; } = HuntDirection.Forward;

    /// <summary>寶箱巡迴不巡的路線（<see cref="Data.TreasureRoutes"/> 的段，存 <see cref="Enums.Aethernet"/> 的數值）；空＝全部巡。在主視窗選，從下一次按開始生效。</summary>
    public List<Aethernet> HuntRoutesExcluded { get; set; } = [];

    /// <summary>true＝只先開所選路線內的已記錄寶箱；false＝不分路線一律先開。全部路線都巡時沒有作用。</summary>
    public bool HuntChestsInRoutesOnly { get; set; } = false;
}
