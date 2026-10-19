using System.Collections.Generic;
using Ocelot.Commands;
using Ocelot.Modules;

namespace BOCCHI.Commands;

[OcelotCommand]
public class ConfigCommand(Plugin plugin) : OcelotCommand
{
    protected override string Command
    {
        get => "/bocchicfg";
    }

    protected override string Description
    {
        get => @"
開關 BOCCHI 設定視窗
 - /bocchicfg：開關設定視窗
--------------------------------
".Trim();
    }

    protected override IReadOnlyList<string> Aliases
    {
        get => ["/bocchic", "/ochcfg", "/ochc", "/occultcrescenthelperconfig"];
    }


    public override void Execute(string command, string arguments)
    {
        plugin.Windows.ToggleConfigUI();
    }
}
