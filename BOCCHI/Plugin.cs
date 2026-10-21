using System;
using System.IO;
using BOCCHI.Automation;
using BOCCHI.Chains;
using BOCCHI.Data;
using BOCCHI.Ledger;
using BOCCHI.Modules.Automator;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin;
using ECommons;
using ECommons.DalamudServices;
using Ocelot;
using Ocelot.Chain;

namespace BOCCHI;

public sealed class Plugin : OcelotPlugin
{
    public override string Name
    {
        get => "Occult Crescent Helper";
    }

    public Config Config { get; }

    public override IOcelotConfig OcelotConfig
    {
        get => Config;
    }

    public static ChainQueue Chain
    {
        get => ChainManager.Get("OCH##main");
    }

    /// <summary>所有自動化的開始／停止入口；模組建構期間還是 null。</summary>
    public AutomationControl Automation { get; private set; } = null!;

    /// <summary>新月島的場次帳與活動帳（只寫 log）；模組建構期間還是 null。</summary>
    public IslandLedger? Ledger { get; private set; }

    public Plugin(IDalamudPluginInterface plugin)
        : base(plugin, Module.DalamudReflector)
    {
        Config = plugin.GetPluginConfig() as Config ?? new Config();

        SetupLanguage(plugin);

        OcelotInitialize(OcelotFeature.All);

        ChainHelper.Initialize(this);

        Automation = new AutomationControl(this);

        // 換區時要在 AutomationControl 停完自動化之後才結束場次：訂閱順序在它後面。記錄失敗不可以讓插件載入失敗。
        try
        {
            Ledger = new IslandLedger(this);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[BOCCHI][場次記錄錯誤] 初始化失敗，這次載入不寫 [場次]／[活動帳]：{ex.GetType().Name}：{ex.Message}");
        }
    }

    public override void Dispose()
    {
        // 先於模組與動作鏈收掉：卸載當下還在跑的自動化要先把導航停下來。
        Automation?.Dispose();
        Ledger?.Dispose();
        base.Dispose();
    }

    private void SetupLanguage(IDalamudPluginInterface plugin)
    {
        I18N.SetDirectory(plugin.AssemblyLocation.Directory?.FullName!);
        I18N.LoadAllFromDirectory("en", "Translations/en");
        I18N.LoadAllFromDirectory("jp", "Translations/jp");
        I18N.LoadAllFromDirectory("fr", "Translations/fr");
        // Traditional Chinese for the TC (台服) client. Loaded UNCONDITIONALLY -
        // upstream keeps its Simplified "zh" behind #if DALAMUD_CN, which only the
        // Release_CN configuration defines, so on our Release build that whole
        // branch is compiled out and the client falls through to English.
        I18N.LoadAllFromDirectory("tw", "Translations/tw");
        GameTextTokens.Apply("tw", Path.Combine(plugin.AssemblyLocation.Directory?.FullName ?? string.Empty, "Translations", "tw"));
#if DALAMUD_CN
        I18N.LoadAllFromDirectory("zh", "Translations/zh");
#endif

        // @todo: Breakup German and uwu translation
        I18N.LoadFromFile("de", "Translations/de.json");
        I18N.LoadFromFile("uwu", "Translations/uwu.json");

        var lang = Svc.ClientState.ClientLanguage switch
        {
            ClientLanguage.French => "fr",
            ClientLanguage.German => "de",
            ClientLanguage.Japanese => "jp",
#if DALAMUD_CN
            ClientLanguage.ChineseSimplified => "zh",
#endif
            // 數值轉型、不寫列舉成員名：這兩個成員名不是每個 Dalamud 版本都有。
            // 台服回報 7（TraditionalChinese）；5 是 ChineseTraditional，有些版本回報它。
            (ClientLanguage)5 or (ClientLanguage)7 => "tw",
            _ => "en",
        };

        I18N.SetLanguage(lang);

        var today = DateTime.Today;
        if (today is { Month: 4, Day: 1 } && Random.Shared.NextDouble() < 0.05)
        {
            I18N.SetLanguage("uwu");
        }
    }

    // 先記再分派：分派給模組的那一段只送給這一幀有更新的模組（開箱時整個插件不更新）。
    // 玩家頻道不收：別人把系統訊息原句貼出來，不可以被當成自己的魔尋寶結果或第二處財寶提示。
    protected override void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (ZoneData.IsInOccultCrescent() && !PlayerChat.IsPlayerChannel(type))
        {
            var text = message.TextValue;
            var senseBefore = global::BOCCHI.Modules.Treasure.TreasureSenseNotice.Sequence;
            SecondTreasureNotice.Observe(text, Environment.TickCount64);
            global::BOCCHI.Modules.Treasure.TreasureSenseNotice.Observe(text);
            if (!secondTreasureTypeLogged && text.Contains(SecondTreasureNotice.Needle, StringComparison.Ordinal))
            {
                secondTreasureTypeLogged = true;
                LogNoticeType("第二處財寶提示", type);
            }

            if (!senseTypeLogged && global::BOCCHI.Modules.Treasure.TreasureSenseNotice.Sequence != senseBefore)
            {
                senseTypeLogged = true;
                LogNoticeType("魔尋寶結果", type);
            }
        }

        base.OnChatMessage(type, timestamp, ref sender, ref message, ref isHandled);
    }

    private static bool secondTreasureTypeLogged;

    private static bool senseTypeLogged;

    private static void LogNoticeType(string what, XivChatType type)
    {
        Svc.Log.Information($"[BOCCHI][聊天] 第一次收到{what}：訊息類型 {(int)type}（0x{(int)type:X4}，頻道 {(int)type & 0x7F}）；玩家頻道裡的同一句不收（之後不再記）");
    }

    protected override bool ShouldUpdate()
    {
        return ZoneData.IsInOccultCrescent()
               && !(
                   Svc.Condition[ConditionFlag.BetweenAreas] ||
                   Svc.Condition[ConditionFlag.BetweenAreas51] ||
                   Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
                   Svc.Condition[ConditionFlag.OccupiedInEvent] ||
                   Svc.Condition[ConditionFlag.WatchingCutscene] ||
                   Svc.Condition[ConditionFlag.WatchingCutscene78] ||
                   Svc.Objects.LocalPlayer?.IsTargetable != true
               );
    }
}
