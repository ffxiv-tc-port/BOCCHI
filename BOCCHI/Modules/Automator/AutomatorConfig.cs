using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Ocelot.Config.Attributes;
using Ocelot.Modules;

namespace BOCCHI.Modules.Automator;

public class AutomatorConfig : ModuleConfig
{
    [Checkbox]
    [Illegal]
    [RequiredPlugin("Lifestream", "vnavmesh")]
    [Label("generic.label.enabled")]
    [Tooltip("enabled")]
    public bool Enabled { get; set; } = false;

    [Enum(typeof(AiType), nameof(AiTypeProvider))]
        // 🔴 預設是 BMR 不是 VBM：本艦隊出貨的戰鬥 AI 只有 BossModReborn（指令 /bmrai），
    //    feed 裡沒有 veyn 的 Boss Mod（/vbmai）。預設留在 VBM 的話，開箱即用時這個開關
    //    會送出一個不存在的指令 —— 沒有閘門、沒有訊息，使用者只會覺得「戰鬥 AI 沒有動」。
    // ⚠️ 改預設只對全新安裝有效：這個鍵一旦寫進使用者的設定檔，反序列化就會覆蓋欄位初始值，
    //    既有使用者要自己在 UI 切一次（刻意不做一次性遷移 —— 那會回退他自己選過的值）。
    public AiType AiProvider { get; set; } = AiType.BMR;

    [Checkbox] public bool ToggleAiProvider { get; set; } = true;

    public bool ShouldToggleAiProvider
    {
        get => IsPropertyEnabled(nameof(ToggleAiProvider));
    }

    [Checkbox] public bool ForceTarget { get; set; } = true;

    public bool ShouldForceTarget
    {
        get => IsPropertyEnabled(nameof(ForceTarget));
    }

    [Checkbox]
    [DependsOn(nameof(ForceTarget))]

    public bool ForceTargetCentralEnemy { get; set; } = true;

    public bool ShouldForceTargetCentralEnemy
    {
        get => IsPropertyEnabled(nameof(ForceTargetCentralEnemy));
    }

    /// <summary>別的插件或你自己選的合法目標至少保留這麼多秒才換回；0＝舊行為（每 0.5 秒改寫，敵人清單空時清空目標）。</summary>
    [IntRange(0, 10)]
    [DependsOn(nameof(ForceTarget))]
    [Indent]
    public int TargetMinHoldSeconds { get; set; } = 3;

    [FloatRange(5f, 30f)] public float EngagementRange { get; set; } = 5f;

    /// <summary>F.A.T.E. 的魔物已經在戰鬥中時，遠程與治療職業到下方距離內就下坐騎接戰；防護與近戰職業照舊用接戰距離。</summary>
    [Checkbox]
    public bool EarlyEngageWhenFateMobEngaged { get; set; } = true;

    public bool ShouldEarlyEngageWhenFateMobEngaged
    {
        get => IsPropertyEnabled(nameof(EarlyEngageWhenFateMobEngaged));
    }

    /// <summary>遠程與治療職業提早接戰的距離（米，算到魔物的碰撞圈）。</summary>
    [FloatRange(5f, 25f)]
    [DependsOn(nameof(EarlyEngageWhenFateMobEngaged))]
    [Indent]
    public float RangedEngagementRange { get; set; } = 20f;

    [Checkbox] public bool ManageWrathOccultOptions { get; set; } = false;

    /// <summary>清單與 WrathCombo 自己的輔助職業技能開關保持一致（清單的改動會寫進 WrathCombo 的設定檔）；false＝照舊，兩邊互不影響。</summary>
    [Checkbox]
    [DependsOn(nameof(ManageWrathOccultOptions))]
    [Indent]
    public bool SyncWrathOccultOptions { get; set; } = false;

    /// <summary>總開關打開時，BOCCHI 執行期間 WrathCombo 可以用的輔助職業技能（WrathCombo 的選項名）；沒勾的一律關閉。</summary>
    [PhantomActionAllowList]
    [DependsOn(nameof(ManageWrathOccultOptions))]
    [Label("modules.automator.config.allowed_wrath_occult_options.label")]
    public HashSet<string> AllowedWrathOccultOptions { get; set; } = [];

    // 舊設定裡語意相反的禁用清單：只讀不寫（沒有 getter），下次存檔就從設定檔消失。
    [JsonProperty("DisabledWrathOccultOptions")]
    private HashSet<string>? LegacyDisabledWrathOccultOptions
    {
        set => legacyDisabledWrathOccultOptions = value;
    }

    private HashSet<string>? legacyDisabledWrathOccultOptions;

    /// <summary>取走舊禁用清單的內容（啟動時記 log 用）；沒有或是空的回 null。</summary>
    public HashSet<string>? TakeLegacyDisabledWrathOccultOptions()
    {
        var legacy = legacyDisabledWrathOccultOptions;
        legacyDisabledWrathOccultOptions = null;
        return legacy is { Count: > 0 } ? legacy : null;
    }

    [Checkbox] public bool AutoRepair { get; set; } = false;

    public bool ShouldAutoRepair
    {
        get => IsPropertyEnabled(nameof(AutoRepair));
    }

    /// <summary>可以自己修的裝備裡，最低耐久度（%）降到這個值以下（含）就修。</summary>
    [IntRange(1, 99)]
    [DependsOn(nameof(AutoRepair))]
    [Indent]
    public int AutoRepairThreshold { get; set; } = 30;

    /// <summary>自動化因為倒地全部停止後，復活時提示並提供恢復鈕（按下才恢復）；false＝照舊，不提示。</summary>
    [Checkbox] public bool PromptRestoreAfterDeath { get; set; } = false;

    /// <summary>趕路時導航停了、角色卻持續自己移動（使用者在操作）就先不重送，靜止後再接續；false＝照舊每次停下都重送。</summary>
    [Checkbox] public bool YieldToManualMovement { get; set; } = true;

    /// <summary>手動停止非法模式後 2 分鐘內又手動開始＝換一場：停止當下選定的那一場加進本場略過。false＝開回來照舊可能又選到同一場。</summary>
    [Checkbox] public bool SkipActivityOnQuickRestart { get; set; } = true;

    public bool ShouldSkipActivityOnQuickRestart
    {
        get => IsPropertyEnabled(nameof(SkipActivityOnQuickRestart));
    }

    // 常用組合：主視窗「開始組合」一次開始的項目。會自己移動的四項彼此互斥，勾兩項以上時按鈕停用並說明。
    [Checkbox] public bool ComboIllegalMode { get; set; } = true;

    [Checkbox] public bool ComboMobFarmer { get; set; } = false;

    [Checkbox] public bool ComboTreasureHunt { get; set; } = false;

    [Checkbox] public bool ComboCarrotHunt { get; set; } = false;

    [Checkbox] public bool ComboReturnAfterEvents { get; set; } = true;

    [Checkbox] public bool ComboPhantomJobLeveling { get; set; } = true;

    [Checkbox]
    [Experimental]
    public bool PrioritizeSilverChests { get; set; } = false;

    public bool ShouldPrioritizeSilverChests
    {
        get => IsPropertyEnabled(nameof(PrioritizeSilverChests));
    }

    /// <summary>白銀寶箱離玩家（直線，米）不超過這個值才去開。</summary>
    [IntRange(10, 200)]
    [RangeIndicator(0.753f, 0.753f, 0.753f)]
    [DependsOn(nameof(PrioritizeSilverChests))]
    [Indent]
    public int SilverChestMaxDistance { get; set; } = 50;

    /// <summary>已經有要去的活動時，先繞去寶箱比直接過去多走（直線，米）不超過這個值才去開。</summary>
    [IntRange(0, 200)]
    [DependsOn(nameof(PrioritizeSilverChests))]
    [Indent]
    public int SilverChestMaxDetour { get; set; } = 40;

    // Critical Encounters
    [Checkbox] public bool DoCriticalEncounters { get; set; } = true;

    public bool ShouldDoCriticalEncounters
    {
        get => IsPropertyEnabled(nameof(DoCriticalEncounters));
    }

    [Checkbox]
    [DependsOn(nameof(DoCriticalEncounters))]

    public bool DelayCriticalEncounters { get; set; } = false;

    public bool ShouldDelayCriticalEncounters
    {
        get => IsPropertyEnabled(nameof(DelayCriticalEncounters));
    }

    [Checkbox]
    [DependsOn(nameof(DoCriticalEncounters), nameof(DoFates))]
    public bool LeaveFateTravelForCriticalEncounter { get; set; } = false;

    public bool ShouldLeaveFateTravelForCriticalEncounter
    {
        get => IsPropertyEnabled(nameof(LeaveFateTravelForCriticalEncounter));
    }

    /// <summary>剩餘報名秒數不超過這個值才改去；0＝只要正在報名就改去。</summary>
    [IntRange(0, 180)]
    [DependsOn(nameof(DoCriticalEncounters), nameof(DoFates), nameof(LeaveFateTravelForCriticalEncounter))]
    [Indent]
    public int LeaveFateTravelForCriticalEncounterSeconds { get; set; } = 90;

    [Checkbox]
    [DependsOn(nameof(DoCriticalEncounters))]
    [Indent]

    public bool DoScourgeOfTheMind { get; set; } = true;

    public bool ShouldDoScourgeOfTheMind
    {
        get => IsPropertyEnabled(nameof(DoScourgeOfTheMind));
    }

    [Checkbox]
    [DependsOn(nameof(DoCriticalEncounters))]
    [Indent]

    public bool DoTheBlackRegiment { get; set; } = true;

    public bool ShouldDoTheBlackRegiment
    {
        get => IsPropertyEnabled(nameof(DoTheBlackRegiment));
    }

    [Checkbox]
    [DependsOn(nameof(DoCriticalEncounters))]
    [Indent]

    public bool DoTheUnbridled { get; set; } = true;

    public bool ShouldDoTheUnbridled
    {
        get => IsPropertyEnabled(nameof(DoTheUnbridled));
    }

    [Checkbox]
    [DependsOn(nameof(DoCriticalEncounters))]
    [Indent]

    public bool DoCrawlingDeath { get; set; } = true;

    public bool ShouldDoCrawlingDeath
    {
        get => IsPropertyEnabled(nameof(DoCrawlingDeath));
    }

    [Checkbox]
    [DependsOn(nameof(DoCriticalEncounters))]
    [Indent]

    public bool DoCalamityBound { get; set; } = true;

    public bool ShouldDoCalamityBound
    {
        get => IsPropertyEnabled(nameof(DoCalamityBound));
    }

    [Checkbox]
    [DependsOn(nameof(DoCriticalEncounters))]
    [Indent]

    public bool DoTrialByClaw { get; set; } = true;

    public bool ShouldDoTrialByClaw
    {
        get => IsPropertyEnabled(nameof(DoTrialByClaw));
    }

    [Checkbox]
    [DependsOn(nameof(DoCriticalEncounters))]
    [Indent]

    public bool DoFromTimesBygone { get; set; } = true;

    public bool ShouldDoFromTimesBygone
    {
        get => IsPropertyEnabled(nameof(DoFromTimesBygone));
    }

    [Checkbox]
    [DependsOn(nameof(DoCriticalEncounters))]
    [Indent]

    public bool DoCompanyOfStone { get; set; } = true;

    public bool ShouldDoCompanyOfStone
    {
        get => IsPropertyEnabled(nameof(DoCompanyOfStone));
    }

    [Checkbox]
    [DependsOn(nameof(DoCriticalEncounters))]
    [Indent]

    public bool DoSharkAttack { get; set; } = true;

    public bool ShouldDoSharkAttack
    {
        get => IsPropertyEnabled(nameof(DoSharkAttack));
    }

    [Checkbox]
    [Indent]
    [DependsOn(nameof(DoCriticalEncounters))]

    public bool DoOnTheHunt { get; set; } = true;

    public bool ShouldDoOnTheHunt
    {
        get => IsPropertyEnabled(nameof(DoOnTheHunt));
    }

    [Checkbox]
    [DependsOn(nameof(DoCriticalEncounters))]
    [Indent]

    public bool DoWithExtremePrejudice { get; set; } = true;

    public bool ShouldDoWithExtremePrejudice
    {
        get => IsPropertyEnabled(nameof(DoWithExtremePrejudice));
    }

    [Checkbox]
    [DependsOn(nameof(DoCriticalEncounters))]
    [Indent]

    public bool DoNoiseComplaint { get; set; } = true;

    public bool ShouldDoNoiseComplaint
    {
        get => IsPropertyEnabled(nameof(DoNoiseComplaint));
    }

    [Checkbox]
    [DependsOn(nameof(DoCriticalEncounters))]
    [Indent]

    public bool DoCursedConcern { get; set; } = true;

    public bool ShouldDoCursedConcern
    {
        get => IsPropertyEnabled(nameof(DoCursedConcern));
    }

    [Checkbox]
    [DependsOn(nameof(DoCriticalEncounters))]
    [Indent]

    public bool DoEternalWatch { get; set; } = true;

    public bool ShouldDoEternalWatch
    {
        get => IsPropertyEnabled(nameof(DoEternalWatch));
    }

    [Checkbox]
    [DependsOn(nameof(DoCriticalEncounters))]
    [Indent]

    public bool DoFlameOfDusk { get; set; } = true;

    public bool ShouldDoFlameOfDusk
    {
        get => IsPropertyEnabled(nameof(DoFlameOfDusk));
    }

    // Fates
    [Checkbox] public bool DoFates { get; set; } = true;

    public bool ShouldDoFates
    {
        get => IsPropertyEnabled(nameof(DoFates));
    }

    [Checkbox]
    [Indent]
    [DependsOn(nameof(DoFates))]

    public bool DoRoughWaters { get; set; } = true;

    public bool ShouldDoRoughWaters
    {
        get => IsPropertyEnabled(nameof(DoRoughWaters));
    }

    [Checkbox]
    [Indent]
    [DependsOn(nameof(DoFates))]

    public bool DoTheGoldenGuardian { get; set; } = true;

    public bool ShouldDoTheGoldenGuardian
    {
        get => IsPropertyEnabled(nameof(DoTheGoldenGuardian));
    }

    [Checkbox]
    [Indent]
    [DependsOn(nameof(DoFates))]

    public bool DoKingOfTheCrescent { get; set; } = true;

    public bool ShouldDoKingOfTheCrescent
    {
        get => IsPropertyEnabled(nameof(DoKingOfTheCrescent));
    }

    [Checkbox]
    [Indent]
    [DependsOn(nameof(DoFates))]
    [Experimental]

    public bool DoTheWingedTerror { get; set; } = false;

    public bool ShouldDoTheWingedTerror
    {
        get => IsPropertyEnabled(nameof(DoTheWingedTerror));
    }

    [Checkbox]
    [Indent]
    [DependsOn(nameof(DoFates))]

    public bool DoAnUnendingDuty { get; set; } = true;

    public bool ShouldDoAnUnendingDuty
    {
        get => IsPropertyEnabled(nameof(DoAnUnendingDuty));
    }

    [Checkbox]
    [Indent]
    [DependsOn(nameof(DoFates))]

    public bool DoBrainDrain { get; set; } = true;

    public bool ShouldDoBrainDrain
    {
        get => IsPropertyEnabled(nameof(DoBrainDrain));
    }

    [Checkbox]
    [Indent]
    [DependsOn(nameof(DoFates))]

    public bool DoADelicateBalance { get; set; } = true;

    public bool ShouldDoADelicateBalance
    {
        get => IsPropertyEnabled(nameof(DoADelicateBalance));
    }

    [Checkbox]
    [Indent]
    [DependsOn(nameof(DoFates))]

    public bool DoSwornToSoil { get; set; } = true;

    public bool ShouldDoSwornToSoil
    {
        get => IsPropertyEnabled(nameof(DoSwornToSoil));
    }

    [Checkbox]
    [Indent]
    [DependsOn(nameof(DoFates))]

    public bool DoAPryingEye { get; set; } = true;

    public bool ShouldDoAPryingEye
    {
        get => IsPropertyEnabled(nameof(DoAPryingEye));
    }

    [Checkbox]
    [Indent]
    [DependsOn(nameof(DoFates))]

    public bool DoFatalAllure { get; set; } = true;

    public bool ShouldDoFatalAllure
    {
        get => IsPropertyEnabled(nameof(DoFatalAllure));
    }

    [Checkbox]
    [Indent]
    [DependsOn(nameof(DoFates))]

    public bool DoServingDarkness { get; set; } = true;

    public bool ShouldDoServingDarkness
    {
        get => IsPropertyEnabled(nameof(DoServingDarkness));
    }

    [Checkbox]
    [Experimental]
    [Indent]
    [DependsOn(nameof(DoFates))]

    public bool DoPersistentPots { get; set; } = false;

    public bool ShouldDoPersistentPots
    {
        get => IsPropertyEnabled(nameof(DoPersistentPots));
    }

    [Checkbox]
    [Experimental]
    [Indent]
    [DependsOn(nameof(DoFates))]

    public bool DoPleadingPots { get; set; } = false;

    public bool ShouldDoPleadingPots
    {
        get => IsPropertyEnabled(nameof(DoPleadingPots));
    }

    /// <summary>魔法甕 F.A.T.E.＞緊急遭遇戰＞其他 F.A.T.E.（選下一個活動、趕路中改道）；只看上方有勾選參加的魔法甕。false＝照舊緊急遭遇戰優先。</summary>
    [Checkbox]
    [Indent]
    [DependsOn(nameof(DoFates))]
    public bool PrioritizePots { get; set; } = false;

    public bool ShouldPrioritizePots
    {
        get => IsPropertyEnabled(nameof(PrioritizePots));
    }

    /// <summary>魔法甕優先開著時，推估下一個有勾選的魔法甕在這麼多分鐘內出現就不選緊急遭遇戰；0＝不跳過。不知道下一次何時出現時照常參加。</summary>
    [IntRange(0, 15)]
    [Indent]
    [DependsOn(nameof(DoFates), nameof(DoCriticalEncounters), nameof(PrioritizePots))]
    public int SkipCriticalEncountersBeforePotMinutes { get; set; } = 5;

    /// <summary>魔法甕優先開著時，推估下一個有勾選的魔法甕在這麼多分鐘內出現就不接新的一般 F.A.T.E.（照常待命等魔法甕）；0＝不跳過。已經在前往或參加的不拋下。</summary>
    [IntRange(0, 15)]
    [Indent]
    [DependsOn(nameof(DoFates), nameof(PrioritizePots))]
    public int SkipFatesBeforePotMinutes { get; set; } = 2;

    /// <summary>上面兩個窗（取大的）內沒有可接的活動時，先走到推估的下一個魔法甕的出生點附近、挑不在仇恨範圍內的點等；false＝照舊待命。</summary>
    [Checkbox]
    [Indent]
    [DependsOn(nameof(DoFates), nameof(PrioritizePots))]
    public bool WaitAtPotSpawn { get; set; } = true;

    public bool ShouldWaitAtPotSpawn
    {
        get => IsPropertyEnabled(nameof(WaitAtPotSpawn));
    }

    // CriticalEncountersMap／FatesMap 與每幀用的 TryGet… 共用這兩張表；順序＝原本字典的順序，存檔的 JSON 不變。
    private static readonly (uint Id, Func<AutomatorConfig, bool> Enabled)[] CriticalEncounterSwitches =
    [
        (33, static c => c.ShouldDoScourgeOfTheMind),
        (34, static c => c.ShouldDoTheBlackRegiment),
        (35, static c => c.ShouldDoTheUnbridled),
        (36, static c => c.ShouldDoCrawlingDeath),
        (37, static c => c.ShouldDoCalamityBound),
        (38, static c => c.ShouldDoTrialByClaw),
        (39, static c => c.ShouldDoFromTimesBygone),
        (40, static c => c.ShouldDoCompanyOfStone),
        (41, static c => c.ShouldDoSharkAttack),
        (42, static c => c.ShouldDoOnTheHunt),
        (43, static c => c.ShouldDoWithExtremePrejudice),
        (44, static c => c.ShouldDoNoiseComplaint),
        (45, static c => c.ShouldDoCursedConcern),
        (46, static c => c.ShouldDoEternalWatch),
        (47, static c => c.ShouldDoFlameOfDusk),
    ];

    private static readonly (uint Id, Func<AutomatorConfig, bool> Enabled)[] FateSwitches =
    [
        (1962, static c => c.ShouldDoRoughWaters),
        (1963, static c => c.ShouldDoTheGoldenGuardian),
        (1964, static c => c.ShouldDoKingOfTheCrescent),
        (1965, static c => c.ShouldDoTheWingedTerror),
        (1966, static c => c.ShouldDoAnUnendingDuty),
        (1967, static c => c.ShouldDoBrainDrain),
        (1968, static c => c.ShouldDoADelicateBalance),
        (1969, static c => c.ShouldDoSwornToSoil),
        (1970, static c => c.ShouldDoAPryingEye),
        (1971, static c => c.ShouldDoFatalAllure),
        (1972, static c => c.ShouldDoServingDarkness),
        (1976, static c => c.ShouldDoPersistentPots),
        (1977, static c => c.ShouldDoPleadingPots),
    ];

    public IReadOnlyDictionary<uint, bool> CriticalEncountersMap
    {
        get => ToMap(CriticalEncounterSwitches);
    }

    public IReadOnlyDictionary<uint, bool> FatesMap
    {
        get => ToMap(FateSwitches);
    }

    /// <summary>同 <see cref="CriticalEncountersMap"/> 的 TryGetValue，但不配置（每幀的查詢用）。</summary>
    public bool TryGetCriticalEncounterEnabled(uint id, out bool enabled)
    {
        return TryGet(CriticalEncounterSwitches, id, out enabled);
    }

    /// <summary>同 <see cref="FatesMap"/> 的 TryGetValue，但不配置（每幀的查詢用）。</summary>
    public bool TryGetFateEnabled(uint id, out bool enabled)
    {
        return TryGet(FateSwitches, id, out enabled);
    }

    private Dictionary<uint, bool> ToMap((uint Id, Func<AutomatorConfig, bool> Enabled)[] switches)
    {
        var map = new Dictionary<uint, bool>();
        foreach (var (id, enabled) in switches)
        {
            map.Add(id, enabled(this));
        }

        return map;
    }

    private bool TryGet((uint Id, Func<AutomatorConfig, bool> Enabled)[] switches, uint id, out bool enabled)
    {
        foreach (var (key, value) in switches)
        {
            if (key == id)
            {
                enabled = value(this);
                return true;
            }
        }

        enabled = false;
        return false;
    }
}
