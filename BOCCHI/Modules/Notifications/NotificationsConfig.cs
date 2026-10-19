using Ocelot.Config.Attributes;
using Ocelot.Modules;

namespace BOCCHI.Modules.Notifications;

public class NotificationsConfig : ModuleConfig
{
    [Checkbox] public bool NotifyCriticalEncounters { get; set; } = false;

    [Checkbox] public bool NotifyPotFates { get; set; } = false;

    [Checkbox] public bool NotifyWhenGameFocused { get; set; } = false;

    /// <summary>緊急遭遇戰成功、知見等級提升、發現白銀寶箱時請 TataruPraise 念一句；false＝不呼叫。</summary>
    /// <remarks>存檔留在這裡；設定畫面在緊急遭遇戰設定頁（CriticalEncountersConfig.PraiseOccultEvents 讀寫這一個值）。</remarks>
    public bool PraiseOccultEvents { get; set; } = false;
}
