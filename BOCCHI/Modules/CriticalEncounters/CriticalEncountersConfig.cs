using System.Runtime.Serialization;
using Ocelot.Config.Attributes;
using Ocelot.Modules;

namespace BOCCHI.Modules.CriticalEncounters;

public class CriticalEncountersConfig : ModuleConfig
{
    [Checkbox]
    [Label("generic.label.enabled")]
    public bool Enabled { get; set; } = true;

    [Checkbox] public bool TrackForkedTower { get; set; } = true;

    [Checkbox] public bool LogSpawn { get; set; } = false;

    [Checkbox] public bool AlertAll { get; set; } = false;

    [Checkbox] public bool AlertAzurite { get; set; } = false;

    [Checkbox] public bool AlertVerdigris { get; set; } = false;

    [Checkbox] public bool AlertMalachite { get; set; } = false;

    [Checkbox] public bool AlertRealgar { get; set; } = false;

    [Checkbox] public bool AlertCaputMortuum { get; set; } = false;

    [Checkbox] public bool AlertOrpiment { get; set; } = false;

    [Checkbox] public bool AlertOracle { get; set; } = false;

    [Checkbox] public bool AlertBerserker { get; set; } = false;

    [Checkbox] public bool AlertRanger { get; set; } = false;

    [Checkbox] public bool ShowHistory { get; set; } = false;

    /// <summary>前往報名（非法模式、主視窗跑過去／傳送後走過去、跟團刷 CE）時站到報名範圍內離其他玩家夠遠的隨機一點；false＝照原本的目的地。</summary>
    [Checkbox] public bool RandomRegistrationSpot { get; set; } = true;

    /// <summary>跟團刷 CE 的目標（DynamicEvent id）；0＝還沒選。在主視窗選，下一次武裝生效。</summary>
    public uint CeFarmTarget { get; set; } = 0;

    /// <summary>跟團刷 CE 的輪數；0＝不限。在主視窗設定，下一次武裝生效。</summary>
    public int CeFarmRounds { get; set; } = 0;

    /// <summary>跟團刷 CE：進島後等目標開放報名的上限（分鐘），超過就解除武裝。</summary>
    [IntRange(1, 30)]
    public int CeFarmWaitMinutes { get; set; } = 5;

    /// <summary>只負責畫在本頁：讀寫 NotificationsConfig.PraiseOccultEvents（存檔在那裡），本身不進存檔。</summary>
    [Checkbox]
    [IgnoreDataMember]
    public bool PraiseOccultEvents
    {
        get => Owner is Module module && module.PluginConfig.NotificationsConfig.PraiseOccultEvents;
        set
        {
            if (Owner is Module module)
            {
                module.PluginConfig.NotificationsConfig.PraiseOccultEvents = value;
            }
        }
    }
}
