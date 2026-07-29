using System;
using BOCCHI.Chains;
using BOCCHI.Data;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
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

    public Plugin(IDalamudPluginInterface plugin)
        : base(plugin, Module.DalamudReflector)
    {
        Config = plugin.GetPluginConfig() as Config ?? new Config();

        SetupLanguage(plugin);

        OcelotInitialize(OcelotFeature.All);

        ChainHelper.Initialize(this);
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
            // 🔴 Numeric casts, never the enum NAMES. TC reports 7
            // (TraditionalChinese) since Dalamud 13.0.0.16, but the pin CI builds
            // against (13.0.0.6) does not have that name at all - spelling it out
            // compiles locally and fails in CI. 5 is ChineseTraditional, which
            // some builds report instead.
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
                   Svc.ClientState.LocalPlayer?.IsTargetable != true
               );
    }
}
