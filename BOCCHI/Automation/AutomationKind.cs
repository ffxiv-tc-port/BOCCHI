using System;

namespace BOCCHI.Automation;

/// <summary>BOCCHI 裡每一項「會自己動」的自動化；開始與停止一律經過 <see cref="AutomationControl"/>。</summary>
public enum AutomationKind
{
    IllegalMode,
    MobFarmer,
    TreasureHunt,
    CarrotHunt,
    ReturnAfterEvents,
    PhantomJobLeveling,
}

/// <summary>主視窗「開始組合／停止組合」鈕的狀態（<see cref="AutomationControl.GetComboStatus"/>）。</summary>
public enum ComboStatus : byte
{
    /// <summary>非法模式模組沒載入（組合設定在它的設定頁）。</summary>
    Unavailable,

    /// <summary>組合沒有勾選任何項目。</summary>
    Empty,

    /// <summary>勾了兩項以上會自己移動的自動化（彼此互斥，不能同時開）。</summary>
    Conflict,

    /// <summary>還沒在跑的那幾項有一項開不起來。</summary>
    Blocked,

    /// <summary>可以開始（還沒在跑的那幾項都開得起來）。</summary>
    Ready,

    /// <summary>組合裡的項目全部在跑：按鈕變成停止組合。</summary>
    Running,
}

public static class AutomationKindExtensions
{
    // 陣列而不是 IReadOnlyList：framework 每幀都要走訪它，走介面的 foreach 每次都配置一個列舉器。
    public static readonly AutomationKind[] All =
    [
        AutomationKind.IllegalMode,
        AutomationKind.MobFarmer,
        AutomationKind.TreasureHunt,
        AutomationKind.CarrotHunt,
        AutomationKind.ReturnAfterEvents,
        AutomationKind.PhantomJobLeveling,
    ];

    /// <summary>會自己走位、選目標的四項彼此互斥；打完自動返回是一次性的、輔助職業練等只換職業不移動，都可與它們並存。</summary>
    public static bool DrivesMovement(this AutomationKind kind)
    {
        return kind is not (AutomationKind.ReturnAfterEvents or AutomationKind.PhantomJobLeveling);
    }

    /// <summary>可以暫停／續跑的兩項（寶箱與幸運胡蘿蔔巡迴，共用 Hunter）。</summary>
    public static bool SupportsPause(this AutomationKind kind)
    {
        return kind is AutomationKind.TreasureHunt or AutomationKind.CarrotHunt;
    }

    /// <summary>名稱的翻譯鍵。</summary>
    public static string NameKey(this AutomationKind kind)
    {
        return kind switch
        {
            AutomationKind.IllegalMode => "automation.kind.illegal_mode",
            AutomationKind.MobFarmer => "automation.kind.mob_farmer",
            AutomationKind.TreasureHunt => "automation.kind.treasure_hunt",
            AutomationKind.CarrotHunt => "automation.kind.carrot_hunt",
            AutomationKind.ReturnAfterEvents => "automation.kind.return_after_events",
            AutomationKind.PhantomJobLeveling => "automation.kind.phantom_job_leveling",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    /// <summary>主視窗控制列上的短名翻譯鍵；完整名稱（<see cref="NameKey"/>）放在 tooltip。</summary>
    public static string ShortNameKey(this AutomationKind kind)
    {
        return kind switch
        {
            AutomationKind.IllegalMode => "automation.kind_short.illegal_mode",
            AutomationKind.MobFarmer => "automation.kind_short.mob_farmer",
            AutomationKind.TreasureHunt => "automation.kind_short.treasure_hunt",
            AutomationKind.CarrotHunt => "automation.kind_short.carrot_hunt",
            AutomationKind.ReturnAfterEvents => "automation.kind_short.return_after_events",
            AutomationKind.PhantomJobLeveling => "automation.kind_short.phantom_job_leveling",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    /// <summary>指令參數（/bocchi start|stop 後面接的字）。</summary>
    public static string CommandKey(this AutomationKind kind)
    {
        return kind switch
        {
            AutomationKind.IllegalMode => "illegal",
            AutomationKind.MobFarmer => "farm",
            AutomationKind.TreasureHunt => "treasure",
            AutomationKind.CarrotHunt => "carrot",
            AutomationKind.ReturnAfterEvents => "return",
            AutomationKind.PhantomJobLeveling => "leveling",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    public static bool TryParseCommandKey(string key, out AutomationKind kind)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.CommandKey(), key, StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }
}
