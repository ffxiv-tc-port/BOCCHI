using System;
using System.Collections.Generic;
using System.Linq;
using BOCCHI.Chains;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Ledger;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.Fates;
using BOCCHI.Modules.PhantomJobs;
using BOCCHI.Modules.StateManager;
using BOCCHI.Pathfinding;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Ocelot.Chain;
using Ocelot.IPC;

namespace BOCCHI.Modules.Automator;

public partial class Automator
{
    private bool IsSkipped(Fate fate)
    {
        return skips is { Count: > 0 } list && list.Contains(SkipKind.Fate, fate.Id, fate.StartTimeEpoch);
    }

    private bool IsSkipped(in CriticalEncounterSnapshot encounter)
    {
        return skips is { Count: > 0 } list && list.Contains(SkipKind.CriticalEncounter, encounter.DynamicEventId, CeInstanceStart(encounter));
    }

    // 報名截止只在報名中拿來分場次；開打後當成讀不到（同一場），不賭它開打後還是同一個值。換場次一定先回到 Inactive。
    private static int CeInstanceStart(in CriticalEncounterSnapshot encounter)
    {
        return encounter.State == DynamicEventState.Register ? encounter.StartTimestamp : 0;
    }

    /// <summary>「換一場」認得的活動（F.A.T.E.、緊急遭遇戰）是哪一場；等魔法甕不是一場活動，回 false。</summary>
    internal static bool TryGetInstance(Activity? activity, out EventInstance instance)
    {
        switch (activity)
        {
            case FateActivity fate:
                instance = new EventInstance(SkipKind.Fate, fate.data.Id, fate.StartTimeEpoch);
                return true;
            case CriticalEncounter encounter:
                instance = new EventInstance(SkipKind.CriticalEncounter, encounter.data.Id, encounter.StartTimestamp);
                return true;
            case ForkedTowerWaitActivity tower:
                instance = new EventInstance(SkipKind.CriticalEncounter, tower.data.Id, tower.StartTimestamp);
                return true;
            default:
                instance = default;
                return false;
        }
    }

    /// <summary>
    /// 換一場：目前選定的那一場加進本場略過並結束它（中止趕路，收的範圍與停止非法模式相同），下一幀照常選活動。
    /// 非法模式不停；角色已在那一場的範圍內、緊急遭遇戰裡或戰鬥中時不帶角色離開（照既有的接手與等脫離戰鬥規則）。只在框架執行緒呼叫。
    /// </summary>
    public SkipOutcome SkipCurrent(AutomatorModule module, string source, out string name, out SkipPlace place)
    {
        var current = Activity;
        name = current?.GetName() ?? string.Empty;
        place = SkipPlace.Away;
        if (current == null || !TryGetInstance(current, out var instance))
        {
            var outcome = current == null ? SkipOutcome.NoActivity : SkipOutcome.NotAnEvent;
            Svc.Log.Information($"[BOCCHI][換一場] 來源={source} 沒有略過：{(current == null ? "目前沒有選定的活動" : $"目前是「{name}」，不是一場活動")} 略過集合={SkipCount}");
            return outcome;
        }

        place = LocateForSkip(module, instance);
        var state = current.state;
        Skips.Add(instance, name);
        var navigation = module.Plugin.Automation.AbortIllegalModeTravelForSkip();
        current.LogTargetSummary(ActivityEnd.Skipped);
        Ledger?.Ended(current, ActivityEnd.Skipped);
        Activity = null;
        idleTime = 0;
        nextLeaveFateTravelCheckAt = 0;
        nextPotTravelCheckAt = 0;
        nextLeaveWaitCheckAt = 0;
        Svc.Log.Information($"[BOCCHI][換一場] 來源={source} 略過 kind={current.KindLabel} id={current.data.Id} 名=「{name}」 開始={instance.Start} 階段={state} 位置={place} 略過集合={Skips.Count}；導航：{navigation}");
        return SkipOutcome.Skipped;
    }

    // 與接手的判斷同一份資料：所在的 F.A.T.E.（FateManager.CurrentFate）、參加中的緊急遭遇戰（追蹤器當幀抄的 id）。
    private SkipPlace LocateForSkip(AutomatorModule module, EventInstance instance)
    {
        if (instance.Kind == SkipKind.Fate && ReadCurrentFate().Id == instance.Id)
        {
            return SkipPlace.InsideFate;
        }

        if (instance.Kind == SkipKind.CriticalEncounter && module.TryGetModule<CriticalEncountersModule>(out var critical) && critical?.Tracker.CurrentEventId == instance.Id)
        {
            return SkipPlace.InsideCriticalEncounter;
        }

        return Svc.Condition[ConditionFlag.InCombat] ? SkipPlace.InCombat : SkipPlace.Away;
    }

    /// <summary>手動停止非法模式時（狀態重設之前）呼叫：記下停止當下選定的那一場。自動停止（倒地、換區、登出、卸載）不呼叫。</summary>
    public void NoteManualStop()
    {
        var current = Activity;
        if (!TryGetInstance(current, out var instance))
        {
            skips?.NoteManualStop(null, string.Empty, Environment.TickCount64);
            return;
        }

        Skips.NoteManualStop(instance, current!.GetName(), Environment.TickCount64);
        Svc.Log.Debug($"[BOCCHI][換一場] 手動停止時選定 kind={current.KindLabel} id={instance.Id} 名=「{current.GetName()}」：{ActivitySkips.QuickRestartMs / 1000} 秒內又開始就略過它");
    }

    /// <summary>
    /// 開始非法模式（已成功）時呼叫：手動停止後 <see cref="ActivitySkips.QuickRestartMs"/> 內又開始＝想換一場，停止當下選定的那一場加進本場略過。
    /// 回傳加進去的名稱（給聊天視窗）；沒有加回 null。停止記錄不論加不加都用掉。只在框架執行緒呼叫。
    /// </summary>
    public string? ApplyQuickRestart(AutomatorModule module, string source, out long elapsedMs)
    {
        elapsedMs = 0;
        if (skips is not { HasPendingStop: true } list)
        {
            return null;
        }

        var quick = list.TakeQuickRestart(Environment.TickCount64, out var instance, out var name, out elapsedMs);
        var seconds = LedgerRules.Seconds(elapsedMs);
        if (!quick)
        {
            Svc.Log.Debug($"[BOCCHI][換一場] 停止 {seconds} 秒後才開始（超過 {ActivitySkips.QuickRestartMs / 1000} 秒）：不略過「{name}」");
            return null;
        }

        var prefix = $"[BOCCHI][換一場] 來源=停止後很快又開始（{source}；停止 {seconds} 秒後）";
        if (!module.Config.ShouldSkipActivityOnQuickRestart)
        {
            Svc.Log.Information($"{prefix} 設定關閉：不略過「{name}」 略過集合={list.Count}");
            return null;
        }

        var live = JudgeLive(module, instance, out _);
        if (live == SkipLiveness.Gone)
        {
            Svc.Log.Information($"{prefix} 停止時選定的「{name}」已經結束：不必略過 略過集合={list.Count}");
            return null;
        }

        if (!list.Add(instance, name))
        {
            Svc.Log.Information($"{prefix} 「{name}」本來就在本場略過裡 略過集合={list.Count}");
            return null;
        }

        var kind = instance.Kind == SkipKind.Fate ? "FATE" : "CE";
        Svc.Log.Information($"{prefix} 略過 kind={kind} id={instance.Id} 名=「{name}」 開始={instance.Start}{(live == SkipLiveness.Unknown ? "（清單讀不到，照樣略過）" : string.Empty)} 略過集合={list.Count}");
        return name;
    }

    /// <summary>每幀呼叫（非法模式關著也要）：略過的那一場結束、從清單消失或已是下一場就拿掉。只在框架執行緒呼叫，沒有略過時不做事。</summary>
    public void PruneSkips(AutomatorModule module)
    {
        if (skips is not { Count: > 0 } list)
        {
            return;
        }

        for (var i = list.Count - 1; i >= 0; i--)
        {
            var live = JudgeLive(module, list.At(i), out var liveStart);
            if (live == SkipLiveness.Live)
            {
                list.FillStart(i, liveStart);
                continue;
            }

            if (live == SkipLiveness.Gone)
            {
                var name = list.NameAt(i);
                list.RemoveAt(i);
                Svc.Log.Information($"[BOCCHI][換一場] 略過的「{name}」已經結束：從本場略過拿掉 略過集合={list.Count}");
            }
        }
    }

    /// <summary>清空本場略過與停止記錄（換區、登出、使用者按清空）；回傳清掉的場數。只在框架執行緒呼叫。</summary>
    public int ClearSkips(string reason)
    {
        if (skips == null)
        {
            return 0;
        }

        var pending = skips.HasPendingStop;
        var count = skips.Clear();
        if (count > 0 || pending)
        {
            Svc.Log.Information($"[BOCCHI][換一場] 清空本場略過（{reason}）：{count} 場{(pending ? "，停止記錄也一起丟掉" : string.Empty)}");
        }

        return count;
    }

    private SkipLiveness JudgeLive(AutomatorModule module, EventInstance instance, out int liveStart)
    {
        liveStart = 0;
        if (instance.Kind == SkipKind.Fate)
        {
            if (!fatesResolved)
            {
                fatesResolved = true;
                module.TryGetModule(out fates);
            }

            if (fates == null)
            {
                return SkipLiveness.Unknown;
            }

            if (!fates.fates.TryGetValue(instance.Id, out var fate))
            {
                return SkipLiveness.Gone;
            }

            liveStart = fate.StartTimeEpoch;
            return ActivitySkips.Judge(true, true, instance.Start, liveStart);
        }

        if (!criticalEncountersResolved)
        {
            criticalEncountersResolved = true;
            module.TryGetModule(out criticalEncounters);
        }

        // 緊急遭遇戰清單每幀整份重抄，拿不到指揮器時是空的：那時判不了，先留著。
        if (criticalEncounters == null || criticalEncounters.CriticalEncounters.Count == 0)
        {
            return SkipLiveness.Unknown;
        }

        if (!criticalEncounters.CriticalEncounters.TryGetValue(instance.Id, out var encounter))
        {
            return SkipLiveness.Gone;
        }

        liveStart = CeInstanceStart(encounter);
        return ActivitySkips.Judge(true, encounter.State != DynamicEventState.Inactive, instance.Start, liveStart);
    }
}
