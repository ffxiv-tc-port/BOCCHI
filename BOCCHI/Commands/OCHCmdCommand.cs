using System;
using System.Collections.Generic;
using BOCCHI.Enums;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.Fates;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Ocelot.Commands;
using Ocelot.Modules;

namespace BOCCHI.Commands;

[OcelotCommand]
public class OCHCmdCommand(Plugin plugin) : OcelotCommand
{
    protected override string Command
    {
        get => "/bocchicmd";
    }

    protected override string Description
    {
        get => @"
Utility command.
 - Flag commands clear active flag before trying to place a new one
   - /bocchicmd flag-active-ce (Place a flag marker on the current Critical Engagement)
   - /bocchicmd flag-active-fate (Place a flag marker on a current Fate)
   - /bocchicmd flag-active-non-pot-fate (Place a flag marker on a current fate that isn't a pot fate)
--------------------------------
".Trim();
    }

    protected override IReadOnlyList<string> Aliases
    {
        get => ["/ochcmd"];
    }

    protected override IReadOnlyList<string> ValidArguments
    {
        get => ["flag-active-ce", "flag-active-fate", "flag-active-non-pot-fate"];
    }

    public override unsafe void Execute(string command, string arguments)
    {
        // 🔴 AgentMap.Instance() 由 [Agent(AgentId.Map)] 產生:內部鏈
        //    AgentModule -> UIModule -> Framework,任一層回 null 整條就回 null(登入前、
        //    切場景時是常態),而底層 [StaticAddress]/[MemberFunction] 特徵碼失配時改為擲
        //    InvalidOperationException——兩種失效模式並存,只擋一種等於假防護。
        //    裸解參考 null 原生指標是 AccessViolationException,在 .NET Core 屬
        //    corrupted-state exception,try/catch 攔不到 ⇒ 只能事前判空。
        //    這裡是聊天指令(低頻),所以判空後寫 Information 讓使用者回報得出來。
        AgentMap* map;
        try
        {
            map = AgentMap.Instance();
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[OCHCmd] 取得 AgentMap 失敗(特徵碼可能失配),本次指令略過:{ex.Message}");
            return;
        }

        if (map == null)
        {
            Svc.Log.Information("[OCHCmd] AgentMap 尚未就緒(通常是還沒進入場景),本次指令略過。");
            return;
        }

        map->FlagMarkerCount = 0;

        switch (arguments)
        {
            case "flag-active-ce": FlagActiveCe(map); break;
            case "flag-active-fate": FlagActiveFate(map, false); break;
            case "flag-active-non-pot-fate": FlagActiveFate(map, true); break;
        }
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

            map->SetFlagMapMarker(Svc.ClientState.TerritoryType, Svc.ClientState.MapId, encounter.MapMarker.Position);
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
