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
    // 只接手角色實際參加中的那一場。CE 結束後仍在戰鬥時狀態會停在 InCriticalEncounter，
    // 那時沒有正在參加的場次，回 null 等戰鬥結束，不去猜「最後一場還沒結束的」。
    // 那一場被略過時也回 null：不接手、不帶角色離開，它結束後照常選活動。
    private unsafe CriticalEncounter? ResumeCriticalEncounter(AutomatorModule module, Lifestream lifestream, VNavmesh vnav)
    {
        if (!module.TryGetModule<CriticalEncountersModule>(out var critical) || critical == null)
        {
            return null;
        }

        var dec = DynamicEventContainer.GetInstance();
        if (dec == null)
        {
            return null;
        }

        uint id = dec->CurrentEventId;
        if (id == 0 || !critical.CriticalEncounters.TryGetValue(id, out var current) || !EventData.CriticalEncounters.TryGetValue(id, out var data))
        {
            return null;
        }

        if (IsSkipped(current))
        {
            ReportSkippedCriticalEncounter(current);
            return null;
        }

        ceSkipNoteKey = 0;
        return new CriticalEncounter(data, lifestream, vnav, module, critical);
    }

    private void ReportSkippedCriticalEncounter(in CriticalEncounterSnapshot encounter)
    {
        var key = ((ulong)encounter.DynamicEventId << 32) | (uint)encounter.StartTimestamp;
        if (key == ceSkipNoteKey)
        {
            return;
        }

        ceSkipNoteKey = key;
        Svc.Log.Information($"[BOCCHI][非法模式] 角色在緊急遭遇戰「{encounter.Name}」（#{encounter.DynamicEventId}）裡，但它被你略過（換一場）：不接手、也不帶你離開，它結束後照常選活動");
    }

    private CriticalEncounter? FindCriticalEncounter(AutomatorModule module, Lifestream lifestream, VNavmesh vnav)
    {
        if (!module.TryGetModule<CriticalEncountersModule>(out var source) || source == null)
        {
            return null;
        }

        foreach (var encounter in source.CriticalEncounters.Values)
        {
            if (!module.Config.TryGetCriticalEncounterEnabled(encounter.DynamicEventId, out var enabled) || !enabled)
            {
                continue;
            }

            if (encounter.State != DynamicEventState.Register || IsSkipped(encounter))
            {
                continue;
            }

            if (!EventData.CriticalEncounters.TryGetValue(encounter.DynamicEventId, out var data))
            {
                continue;
            }

            return new CriticalEncounter(data, lifestream, vnav, module, source);
        }

        return null;
    }

    /// <summary>
    /// 已在 F.A.T.E. 裡、沒有進行中的活動時接手：角色實際所在的那一場（FateManager.CurrentFate，狀態機判 InFate 用的同一個欄位）。
    /// 那一場沒勾選參加、或不在 F.A.T.E. 表上：不接手（回 null，照舊待在原地、不改去別場）；讀不到所在的那一場：照舊取表上第一個有勾選的。
    /// 後兩種各寫一行記錄（同一個理由、同一場只寫一次）。只在框架執行緒呼叫。
    /// </summary>
    private FateActivity? ResumeFate(AutomatorModule module, Lifestream lifestream, VNavmesh vnav)
    {
        if (!fatesResolved)
        {
            fatesResolved = true;
            module.TryGetModule(out fates);
        }

        if (fates == null)
        {
            return null;
        }

        var (fateId, start) = ReadCurrentFate();
        var tracked = fates.fates.TryGetValue(fateId, out var fate);
        var enabled = module.Config.TryGetFateEnabled(fateId, out var on) && on;
        switch (FateResumeRules.Decide(fateId, tracked, enabled))
        {
            case FateResumeChoice.Current when IsSkipped(fate!):
                ReportResume(FateResumeChoice.Skipped, fateId, start, fate!.Name);
                return null;
            case FateResumeChoice.Current:
                resumeNoteKey = 0;
                return new FateActivity(fate!.Data, lifestream, vnav, module, fate);
            case FateResumeChoice.NotEnabled:
                ReportResume(FateResumeChoice.NotEnabled, fateId, start, fate!.Name);
                return null;
            case FateResumeChoice.NotTracked:
                ReportResume(FateResumeChoice.NotTracked, fateId, start, string.Empty);
                return null;
            default:
                var fallback = FindFate(module, lifestream, vnav);
                ReportResume(FateResumeChoice.Unknown, 0, 0, fallback?.GetName() ?? string.Empty);
                return fallback;
        }
    }

    private void ReportResume(FateResumeChoice choice, uint fateId, int start, string name)
    {
        var key = FateResumeRules.NoteKey(choice, fateId, start);
        if (key == resumeNoteKey)
        {
            return;
        }

        resumeNoteKey = key;
        Svc.Log.Information(choice switch
        {
            FateResumeChoice.NotEnabled => $"[BOCCHI][非法模式] 角色在 F.A.T.E.「{name}」（#{fateId}）範圍內，但它沒有勾選參加：不接手、也不改去別場，離開範圍或它結束後照常選活動",
            FateResumeChoice.NotTracked => $"[BOCCHI][非法模式] 角色在 F.A.T.E. #{fateId} 範圍內，但它不在 BOCCHI 的 F.A.T.E. 表上：不接手、也不改去別場，離開範圍或它結束後照常選活動",
            FateResumeChoice.Skipped => $"[BOCCHI][非法模式] 角色在 F.A.T.E.「{name}」（#{fateId}）範圍內，但它被你略過（換一場）：不接手、也不帶你離開，離開範圍或它結束後照常選下一場",
            _ => name.Length > 0
                ? $"[BOCCHI][非法模式] 狀態是在 F.A.T.E. 裡，但讀不到角色所在的那一場：照舊接手表上第一個有勾選的 F.A.T.E.「{name}」"
                : "[BOCCHI][非法模式] 狀態是在 F.A.T.E. 裡，但讀不到角色所在的那一場，表上也沒有有勾選的 F.A.T.E.：不接手",
        });
    }

    // 當幀讀完即丟；FateManager 或 CurrentFate 是 null 回 (0, 0)。
    private static unsafe (uint Id, int Start) ReadCurrentFate()
    {
        var manager = FateManager.Instance();
        if (manager == null || manager->CurrentFate == null)
        {
            return (0, 0);
        }

        var fate = manager->CurrentFate;
        return (fate->FateId, fate->StartTimeEpoch);
    }

    private FateActivity? FindFate(AutomatorModule module, Lifestream lifestream, VNavmesh vnav)
    {
        if (!module.TryGetModule<FatesModule>(out var source) || source == null)
        {
            return null;
        }

        foreach (var fate in source.fates.Values)
        {
            if (!module.Config.TryGetFateEnabled(fate.Id, out var enabled) || !enabled || IsSkipped(fate))
            {
                continue;
            }

            return new FateActivity(fate.Data, lifestream, vnav, module, fate);
        }

        return null;
    }

    // 有勾選參加、正在 F.A.T.E. 表上、沒被略過的魔法甕（1976／1977）；沒有回 null。
    private FateActivity? FindPotFate(AutomatorModule module, Lifestream lifestream, VNavmesh vnav)
    {
        if (!module.TryGetModule<FatesModule>(out var source) || source == null)
        {
            return null;
        }

        foreach (var fate in source.fates.Values)
        {
            if (!PotCycle.IsPot(fate.Id) || !module.Config.TryGetFateEnabled(fate.Id, out var enabled) || !enabled || IsSkipped(fate))
            {
                continue;
            }

            return new FateActivity(fate.Data, lifestream, vnav, module, fate);
        }

        return null;
    }

    /// <summary>
    /// 選下一個活動：魔法甕優先時 魔法甕＞緊急遭遇戰＞其他 F.A.T.E.，否則照舊 緊急遭遇戰＞F.A.T.E.。
    /// 魔法甕優先時，推估下一個魔法甕快出現就不選緊急遭遇戰（<see cref="TryGetPotDueSoon" />）、更快出現時也不選一般 F.A.T.E.（照常待命等魔法甕）。
    /// </summary>
    private Activity? SelectNext(AutomatorModule module, Lifestream lifestream, VNavmesh vnav, out ActivitySource source)
    {
        var config = module.Config;
        source = ActivitySource.Selected;
        if (config.ShouldPrioritizePots && FindPotFate(module, lifestream, vnav) is { } pot)
        {
            source = ActivitySource.PotPriority;
            return pot;
        }

        Activity? next = null;
        if (config.ShouldDoCriticalEncounters)
        {
            if (TryGetPotDueSoon(module, out var potId, out var remaining))
            {
                ReportCriticalEncountersSkippedForPot(module, potId, remaining);
            }
            else
            {
                next = FindCriticalEncounter(module, lifestream, vnav);
            }
        }

        if (next == null && config.ShouldDoFates)
        {
            if (TryGetPotDueWithin(module, config.SkipFatesBeforePotMinutes, out var fatePot, out var fateRemaining))
            {
                ReportFatesSkippedForPot(module, fatePot, fateRemaining);
            }
            else
            {
                next = FindFate(module, lifestream, vnav);
            }
        }

        return next;
    }

    /// <summary>設定「魔法甕前幾分鐘不接緊急遭遇戰」（同 <see cref="TryGetPotDueWithin"/>，另外要開著參加緊急遭遇戰）。</summary>
    private bool TryGetPotDueSoon(AutomatorModule module, out uint potId, out TimeSpan remaining)
    {
        potId = 0;
        remaining = default;
        var config = module.Config;
        return config.ShouldDoCriticalEncounters && TryGetPotDueWithin(module, config.SkipCriticalEncountersBeforePotMinutes, out potId, out remaining);
    }

    /// <summary>
    /// 魔法甕優先開著、推估下一個有勾選參加的魔法甕在 <paramref name="minutes"/> 分鐘內出現時回 true；minutes ≤ 0 回 false。
    /// 不知道下一次何時出現（這一座島還沒看過魔法甕、推估落空過）時回 false＝照常接。只在框架執行緒呼叫。
    /// </summary>
    private bool TryGetPotDueWithin(AutomatorModule module, int minutes, out uint potId, out TimeSpan remaining)
    {
        potId = 0;
        remaining = default;
        var config = module.Config;
        if (minutes <= 0 || !config.ShouldPrioritizePots)
        {
            return false;
        }

        if (!fatesResolved)
        {
            fatesResolved = true;
            module.TryGetModule(out fates);
        }

        return fates != null
               && PotReminder.IsDueWithin(fates.potCycle.Cycle, DateTime.UtcNow, fates.Config.GetPotCyclePeriod(), minutes, out potId, out remaining)
               && config.TryGetFateEnabled(potId, out var enabled) && enabled;
    }

    // 選活動時因魔法甕快出現而沒選的緊急遭遇戰（條件同 FindCriticalEncounter），每一場寫一次記錄。
    private void ReportCriticalEncountersSkippedForPot(AutomatorModule module, uint potId, TimeSpan remaining)
    {
        if (!criticalEncountersResolved)
        {
            criticalEncountersResolved = true;
            module.TryGetModule(out criticalEncounters);
        }

        if (criticalEncounters == null)
        {
            return;
        }

        foreach (var encounter in criticalEncounters.CriticalEncounters.Values)
        {
            if (encounter.State == DynamicEventState.Register
                && module.Config.TryGetCriticalEncounterEnabled(encounter.DynamicEventId, out var enabled) && enabled
                && EventData.CriticalEncounters.ContainsKey(encounter.DynamicEventId))
            {
                ReportPotSkip(module, encounter, null, potId, remaining);
            }
        }
    }

    /// <param name="travellingTo">正在前往的 F.A.T.E.（趕路中改道那條）；null＝選下一個活動時。</param>
    private void ReportPotSkip(AutomatorModule module, CriticalEncounterSnapshot encounter, Activity? travellingTo, uint potId, TimeSpan remaining)
    {
        if (!potSkipReported.Add((encounter.DynamicEventId, encounter.StartTimestamp)))
        {
            return;
        }

        var where = travellingTo == null ? "選下一個活動" : $"前往 F.A.T.E.「{travellingTo.GetName()}」途中";
        Svc.Log.Information(
            $"[BOCCHI][非法模式] {where}：緊急遭遇戰「{encounter.Name}」正在報名，{DescribePotDue(potId, remaining)}（設定：魔法甕前 {module.Config.SkipCriticalEncountersBeforePotMinutes} 分鐘內不接緊急遭遇戰）：不參加這一場");
    }

    // 選活動時因魔法甕快出現而沒接的 F.A.T.E.（條件同 FindFate：有勾選參加；魔法甕本身前面已經處理），每一場寫一次記錄。
    // 開始時間還沒讀到（0）的那幾幀先不寫：鍵要等它，不然同一場會寫兩次。
    private void ReportFatesSkippedForPot(AutomatorModule module, uint potId, TimeSpan remaining)
    {
        if (fates == null)
        {
            return;
        }

        foreach (var fate in fates.fates.Values)
        {
            if (PotCycle.IsPot(fate.Id) || fate.StartTimeEpoch <= 0
                || !module.Config.TryGetFateEnabled(fate.Id, out var enabled) || !enabled
                || !potFateSkipReported.Add((fate.Id, fate.StartTimeEpoch)))
            {
                continue;
            }

            Svc.Log.Information(
                $"[BOCCHI][非法模式] 選下一個活動：F.A.T.E.「{fate.Name}」進行中，{DescribePotDue(potId, remaining)}（設定：魔法甕前 {module.Config.SkipFatesBeforePotMinutes} 分鐘內不接 F.A.T.E.）：先不接，等魔法甕");
        }
    }

    private string DescribePotDue(uint potId, TimeSpan remaining)
    {
        var pot = fates?.potCycle.GetName(potId) ?? $"#{potId}";
        return remaining >= TimeSpan.Zero
            ? $"但推估魔法甕「{pot}」約 {PotCycleTracker.FormatDuration(remaining)} 後出現"
            : $"但魔法甕「{pot}」的推估出現時間已過 {PotCycleTracker.FormatDuration(remaining.Negate())}、還沒看到";
    }
}
