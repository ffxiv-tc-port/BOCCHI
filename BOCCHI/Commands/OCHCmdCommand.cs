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
工具指令。
 - 標記地圖的指令會先清掉地圖上現有的標記再標新的
   - /bocchicmd flag-active-ce：在地圖上標出目前的緊急遭遇戰
   - /bocchicmd flag-active-fate：在地圖上標出一場進行中的 F.A.T.E.
   - /bocchicmd flag-active-non-pot-fate：同上，但略過魔法甕 F.A.T.E.
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
        // AgentMap.Instance() 兩種失效並存：內部鏈 AgentModule -> UIModule -> Framework 任一層是 null 就回 null（登入前、切場景時是常態），
        // 底層特徵碼失配時改為擲 InvalidOperationException；只擋一種等於假防護。
        // 裸解參考 null 原生指標是 try/catch 攔不到的 AccessViolationException，所以事前判空。
        // 聊天指令（低頻）：判空後寫 Information，要使用者回報的診斷。
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
