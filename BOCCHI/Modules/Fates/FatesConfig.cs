using System;
using Ocelot.Config.Attributes;
using Ocelot.Modules;

namespace BOCCHI.Modules.Fates;

public class FatesConfig : ModuleConfig
{
    [Checkbox]
    [Label("generic.label.enabled")]
    public bool Enabled { get; set; } = true;

    [Checkbox] public bool LogSpawn { get; set; } = false;

    [Checkbox] public bool AlertAll { get; set; } = false;

    [Checkbox] public bool AlertAzurite { get; set; } = false;

    [Checkbox] public bool AlertVerdigris { get; set; } = false;

    [Checkbox] public bool AlertMalachite { get; set; } = false;

    [Checkbox] public bool AlertRealgar { get; set; } = false;

    [Checkbox] public bool AlertCaputMortuum { get; set; } = false;

    [Checkbox] public bool AlertOrpiment { get; set; } = false;

    [Checkbox] public bool ShowPotTimer { get; set; } = false;

    [IntRange(20, 60)]
    [DependsOn(nameof(ShowPotTimer))]
    [Indent]
    public int PotCycleMinutes { get; set; } = 30;

    /// <summary>魔法甕交替週期；設定檔被手改成不合理的值時夾回 1～600 分。寫成方法是為了不被序列化進設定檔。</summary>
    public TimeSpan GetPotCyclePeriod()
    {
        return TimeSpan.FromMinutes(Math.Clamp(PotCycleMinutes, 1, 600));
    }

    /// <summary>推估下一個魔法甕快出現時在聊天視窗提醒一次、響提示音；不知道下一次何時出現時不提醒。不看「顯示魔法甕出現推估」。</summary>
    [Checkbox] public bool AlertPotSoon { get; set; } = true;

    /// <summary>推估出現前幾分鐘提醒。</summary>
    [IntRange(1, 15)]
    [DependsOn(nameof(AlertPotSoon))]
    [Indent]
    public int PotSoonMinutes { get; set; } = 5;

    /// <summary>魔法甕出現（或進島後第一次看到進行中的魔法甕）時在聊天視窗提醒一次、響提示音。</summary>
    [Checkbox] public bool AlertPotSpawned { get; set; } = true;

    /// <summary>兩種魔法甕提醒共用的提示音，1～16 與聊天的 se.1～se.16 相同。</summary>
    [IntRange(1, 16)]
    public int PotAlertSound { get; set; } = 1;
}
