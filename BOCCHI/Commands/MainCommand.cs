using System.Collections.Generic;
using System.Linq;
using BOCCHI.Modules.Debug;
using ECommons;
using ECommons.DalamudServices;
using Ocelot;
using Ocelot.Commands;
using Ocelot.Modules;

namespace BOCCHI.Commands;

[OcelotCommand]
public class MainCommand(Plugin plugin) : OcelotCommand
{
    protected override string Command
    {
        get => "/bocchi";
    }

    protected override string Description
    {
        get => @"
Opens Occult Crescent Helper main ui
 - /bocchi : Opens the main ui
 - /bocchi config : opens the config ui
 - /bocchi cfg : opens the config ui
--------------------------------
".Trim();
    }

    protected override IReadOnlyList<string> Aliases
    {
        get => ["/och", "/occultcrescenthelper"];
    }

    private readonly IReadOnlyList<string> languageCodes =
    [
        "en", "de", "fr", "jp", "uwu",
    ];

    public override void Execute(string command, string arguments)
    {
        if (arguments is "config" or "cfg")
        {
            plugin.Windows.ToggleConfigUI();
            return;
        }

#if DEBUG_BUILD
        if (arguments == "debug")
        {
            plugin.Windows.GetWindow<DebugWindow>().Toggle();
            return;
        }
#endif

        if (arguments == "buff")
        {
            new BuffCommand(plugin).Execute("/bocchibuff", "");
            return;
        }

        if (arguments.StartsWith("tp"))
        {
            new TeleportCommand(plugin).Execute("/bocchitp", arguments.ReplaceFirst("tp", "").Trim());
            return;
        }

        if (arguments.StartsWith("language"))
        {
            var parts = arguments.Split(' ', 2);
            if (parts.Length == 2)
            {
                var code = parts[1].Trim().ToLowerInvariant();
                if (languageCodes.Contains(code))
                {
                    I18N.SetLanguage(code);
                    Svc.Chat.Print(I18N.T("generic.commands.language_set", new Dictionary<string, string> { ["code"] = code }));
                    return;
                }

                Svc.Log.Error($"Unknown language code: {code}");
                return;
            }

            Svc.Chat.Print(I18N.T("generic.commands.language_usage"));
            return;
        }

        plugin.Windows.ToggleMainUI();
    }
}
