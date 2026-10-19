using System.Collections.Generic;
using BOCCHI.Automation;
using BOCCHI.Enums;
using BOCCHI.Modules.Automator;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.Fates;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Ocelot;
using Ocelot.Commands;
using Ocelot.Modules;

namespace BOCCHI.Commands;

[OcelotCommand]
public class OCHIllegalCommand(Plugin plugin) : OcelotCommand
{
    protected override string Command
    {
        get => "/bocchiillegal";
    }

    protected override string Description
    {
        get => @"
管理非法模式（自動參加 F.A.T.E.／緊急遭遇戰）。
 - /bocchiillegal：開關非法模式檢視器
 - /bocchiillegal on：開始非法模式
 - /bocchiillegal off：停止非法模式
 - /bocchiillegal toggle：切換非法模式
--------------------------------
".Trim();
    }

    protected override IReadOnlyList<string> Aliases
    {
        get => ["/ochillegal", "/bocchillegal"];
    }

    protected override IReadOnlyList<string> ValidArguments
    {
        get => ["on", "off", "toggle"];
    }

    public override void Execute(string command, string arguments)
    {
        if (arguments.Trim() == "")
        {
            plugin.Windows.GetWindow<AutomatorWindow>()?.Toggle();
            return;
        }

        var source = I18N.T("automation.source.command");
        switch (arguments)
        {
            case "on":
                plugin.Automation.Start(AutomationKind.IllegalMode, source, true);
                break;
            case "off":
                plugin.Automation.Stop(AutomationKind.IllegalMode, source, true);
                break;
            case "toggle":
                plugin.Automation.Toggle(AutomationKind.IllegalMode, source, true);
                break;
        }

        plugin.Config.Save();
    }

    private unsafe void FlagActiveCe(AgentMap* map)
    {
        if (!plugin.Modules.TryGetModule<CriticalEncountersModule>(out var source) || source == null)
        {
            return;
        }

        foreach (var encounter in source.CriticalEncounters.Values)
        {
            if (encounter.EventType >= 4 || encounter.State != DynamicEventState.Register)
            {
                continue;
            }

            map->SetFlagMapMarker(Svc.ClientState.TerritoryType, Svc.ClientState.MapId, encounter.Position);
            return;
        }
    }

    private unsafe void FlagActiveFate(AgentMap* map, bool ignorePots)
    {
        if (!plugin.Modules.TryGetModule<FatesModule>(out var source) || source == null)
        {
            return;
        }

        foreach (var fate in source.fates.Values)
        {
            if (ignorePots && fate.Data.Note == MonsterNote.PersistentPots)
            {
                continue;
            }

            map->SetFlagMapMarker(Svc.ClientState.TerritoryType, Svc.ClientState.MapId, fate.StartPosition);
            return;
        }
    }
}
