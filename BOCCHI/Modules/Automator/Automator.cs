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

public class Automator
{
    private static bool IsChainActive
    {
        get => ChainManager.Queues.Count > 0;
    }

    public Activity? Activity { get; private set; } = null;

    /// <summary>活動帳；插件建好後才掛上，卸載時拿掉。只在 framework 執行緒用。</summary>
    public IslandLedger? Ledger { get; set; }

    private int idleTime = 0;

    // 兩個活動之間讓輔助職業練等切換的上限；超過就先繼續，一段時間內不再為它暫停（別人的「忙碌中」不可以無上限地擋住非法模式）。
    private const long JobSwitchHoldLimitMs = 20_000;

    private const long JobSwitchHoldCooldownMs = 60_000;

    private PhantomJobsModule? phantomJobs;

    private bool phantomJobsResolved;

    private long jobSwitchHoldSince;

    private long jobSwitchHoldSkipUntil;

    private const long LeaveFateTravelCheckIntervalMs = 500;

    private CriticalEncountersModule? criticalEncounters;

    private bool criticalEncountersResolved;

    private long nextLeaveFateTravelCheckAt;

    private long nextPotTravelCheckAt;

    private long nextLeaveWaitCheckAt;

    // 「已在 F.A.T.E. 裡、不接手或讀不到所在的那一場」上次寫記錄的鍵（FateResumeRules.NoteKey）；0＝沒有。
    private ulong resumeNoteKey;

    // 「前往魔法甕途中不改去緊急遭遇戰」已經對這個活動寫過一次記錄。
    private Activity? potKeptReportedFor;

    private FatesModule? fates;

    private bool fatesResolved;

    // 「魔法甕快出現、不接這一場緊急遭遇戰」已寫過記錄的場次：(id, 報名截止的 Unix 秒)。
    private readonly HashSet<(ushort, int)> potSkipReported = [];

    // 「魔法甕快出現、先不接這一場 F.A.T.E.」已寫過記錄的場次：(id, 開始的 Unix 秒)。
    private readonly HashSet<(uint, int)> potFateSkipReported = [];

    // 本場略過；非法模式停止時不清（停了又開也要記得），換區、登出、使用者清空才清。null＝還沒略過過。
    private ActivitySkips? skips;

    // 「角色在被略過的緊急遭遇戰裡、不接手」上次寫記錄的鍵；0＝沒有。
    private ulong ceSkipNoteKey;

    /// <summary>本場略過（換一場）；第一次用時才建。只在框架執行緒用。</summary>
    public ActivitySkips Skips
    {
        get => skips ??= new ActivitySkips();
    }

    /// <summary>主視窗每幀讀：本場略過的場數，沒建過是 0（不為了讀它而建）。</summary>
    public int SkipCount
    {
        get => skips?.Count ?? 0;
    }

    private readonly SelfRepair selfRepair = new();

    private readonly SilverChestDetour silverChests = new();

    private CombatHoldTimer combatHold;

    // 短於這個秒數的戰鬥閘門等待只寫 Debug（一閃即逝的戰鬥狀態不洗 Information）。
    private const long CombatHoldReportMs = 2000;

    private const long PotHuntPollMs = 250;

    private readonly PotHuntYield potHunt = new();

    private readonly EurekaPotHuntIpc potHuntIpc = new();

    private readonly PotChestSettle potChest = new();

    private long nextPotHuntPollAt;

    private bool potHuntHolding;

    private HandsOnProbe? potHandsOn;

    // 這一輪等尋寶已經中止過自己的趕路（每輪只做一次）。
    private bool potHuntTravelYielded;

    /// <summary>正在等 EurekaHelper 的魔法甕尋寶（先不選活動、不移動）；框架執行緒讀。</summary>
    public bool IsHoldingForPotHunt
    {
        get => potHuntHolding;
    }

    public void PostUpdate(AutomatorModule module, IFramework framework)
    {
        var vnav = module.GetIPCSubscriber<VNavmesh>();
        var lifestream = module.GetIPCSubscriber<Lifestream>();
        if (!vnav.IsReady() || !lifestream.IsReady())
        {
            return;
        }

        if (Svc.Condition[ConditionFlag.InCombat])
        {
            selfRepair.NoteCombat();
        }

        var states = module.GetModule<StateManagerModule>();
        Ledger?.Observe(Activity);

        // 開箱途中（最多約一分鐘）不處理活動失效與換目標，開完再照常往下。
        if (silverChests.Watch(module))
        {
            return;
        }

        if (Activity == null)
        {
            var inCombat = states.GetState() == State.InCombat;
            if (combatHold.Step(inCombat, Environment.TickCount64, out var heldMs))
            {
                ReportCombatHold(heldMs);
            }

            if (inCombat)
            {
                return;
            }

            // 尋寶途中路過別的 F.A.T.E.／緊急遭遇戰也不接手。
            if (HoldForPotHunt(module, states))
            {
                return;
            }

            if (states.GetState() == State.InCriticalEncounter)
            {
                Activity = ResumeCriticalEncounter(module, lifestream, vnav);

                if (Activity != null)
                {
                    module.Debug($"Resuming running activity: {Activity.GetName()}");
                    Ledger?.Selected(Activity, ActivitySource.Resumed);
                }

                return;
            }

            if (states.GetState() == State.InFate)
            {
                Activity = ResumeFate(module, lifestream, vnav);

                if (Activity != null)
                {
                    module.Debug($"Resuming running activity: {Activity.GetName()}");
                    Ledger?.Selected(Activity, ActivitySource.Resumed);
                }

                return;
            }

            resumeNoteKey = 0;
        }

        if (Activity != null && !Activity.IsValid())
        {
            Plugin.Chain.Abort();
            vnav.Stop();
            (Activity as PotWaitActivity)?.ReportEnded();
            Activity.LogTargetSummary(ActivityEnd.Vanished);
            Ledger?.Ended(Activity, ActivityEnd.Vanished);
            ArmPotHuntGrace(Activity);
            Activity = null;
        }

        if ((Activity == null || Activity.state is ActivityState.Idle or ActivityState.Pathfinding or ActivityState.WaitingForPot) && HoldForPotHunt(module, states))
        {
            return;
        }

        if (Activity is PotWaitActivity wait)
        {
            if (TryLeaveWaitForActivity(module, states, wait, lifestream, vnav))
            {
                return;
            }
        }
        else if (Activity != null && TryLeaveTravelForPot(module, states, Activity, lifestream, vnav))
        {
            return;
        }

        if (Activity is FateActivity fate && TryLeaveFateTravelForCriticalEncounter(module, states, fate, lifestream, vnav))
        {
            return;
        }

        if (Activity != null && silverChests.TryStart(module, states, Activity, lifestream, IsChainActive))
        {
            return;
        }

        if (IsChainActive)
        {
            return;
        }

        if (Activity != null)
        {
            if (Activity.state == ActivityState.Done)
            {
                Activity.LogTargetSummary(ActivityEnd.Completed);
                Ledger?.Ended(Activity, ActivityEnd.Completed);
                ArmPotHuntGrace(Activity);
                Activity = null;
                return;
            }

            var chain = Activity.GetChain(states);
            if (chain == null)
            {
                return;
            }

            Plugin.Chain.Submit(chain);
            return;
        }

        if (selfRepair.Tick(module, vnav) || HoldForPhantomJobSwitch(module))
        {
            return;
        }

        if (!module.Config.ShouldDoFates && !module.Config.ShouldDoCriticalEncounters)
        {
            return;
        }

        Activity = SelectNext(module, lifestream, vnav, out var source);
        if (Activity != null)
        {
            Svc.Log.Info($"Selected activity: {Activity.GetName()}");
            Ledger?.Selected(Activity, source);
            return;
        }

        if (silverChests.TryStart(module, states, null, lifestream, false))
        {
            return;
        }

        // 等魔法甕在活動帳上算待命：不記成選了活動。
        if (TryStartPotWait(module, lifestream, vnav))
        {
            Ledger?.IdleTick();
            return;
        }

        Ledger?.IdleTick();
        var closest = AethernetData.GetClosestToPlayer();
        if (closest.DistanceToPlayer() <= 4.5f)
        {
            return;
        }

        idleTime += framework.UpdateDelta.Milliseconds;
        if (idleTime > 3000)
        {
            idleTime = 0;

            Plugin.Chain.Submit(ChainHelper.ReturnChain(new ReturnChainConfig { ApproachAetheryte = true }));
            Ledger?.IdleReturnSubmitted();
        }
    }

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

    /// <summary>
    /// 輔助職業練等要換職業時，兩個活動之間先不選下一個（沒有動作鏈在跑，練等那邊的切換條件才會成立）。
    /// 每次暫停最多 <see cref="JobSwitchHoldLimitMs"/>。
    /// </summary>
    private bool HoldForPhantomJobSwitch(AutomatorModule module)
    {
        if (!phantomJobsResolved)
        {
            phantomJobsResolved = true;
            module.TryGetModule(out phantomJobs);
        }

        var leveling = phantomJobs?.Leveling;
        var now = Environment.TickCount64;
        if (leveling is not { IsRunning: true } || !(leveling.WantsSwitch || leveling.IsSwitching) || now < jobSwitchHoldSkipUntil)
        {
            jobSwitchHoldSince = 0;
            return false;
        }

        if (jobSwitchHoldSince == 0)
        {
            jobSwitchHoldSince = now;
            Svc.Log.Information("[BOCCHI][非法模式] 輔助職業練等要切換職業：先不選下一個活動，等切換完成（最多 20 秒）。");
        }

        if (now - jobSwitchHoldSince <= JobSwitchHoldLimitMs)
        {
            return true;
        }

        jobSwitchHoldSince = 0;
        jobSwitchHoldSkipUntil = now + JobSwitchHoldCooldownMs;
        Svc.Log.Information("[BOCCHI][非法模式] 等輔助職業切換超過 20 秒：先繼續非法模式，60 秒內不再為它暫停。");
        return false;
    }

    /// <summary>
    /// 設定「魔法甕優先」：趕路去緊急遭遇戰或別的 F.A.T.E.（還沒進入、沒在戰鬥）時出現有勾選參加的魔法甕，就中止趕路改去魔法甕。
    /// 已經在參加的活動不拋下；返回／傳送的詠唱與讀取中不打斷（同改去緊急遭遇戰）。
    /// </summary>
    private bool TryLeaveTravelForPot(AutomatorModule module, StateManagerModule states, Activity current, Lifestream lifestream, VNavmesh vnav)
    {
        if (!module.Config.ShouldPrioritizePots
            || (current is FateActivity && PotCycle.IsPot(current.data.Id))
            || current.state is not (ActivityState.Idle or ActivityState.Pathfinding)
            || states.GetState() != State.Idle)
        {
            return false;
        }

        var now = Environment.TickCount64;
        if (now < nextPotTravelCheckAt)
        {
            return false;
        }

        nextPotTravelCheckAt = now + LeaveFateTravelCheckIntervalMs;
        if (IsCastingOrBusy() || IsLifestreamBusy(lifestream))
        {
            return false;
        }

        var pot = FindPotFate(module, lifestream, vnav);
        if (pot == null)
        {
            return false;
        }

        var navigation = module.Plugin.Automation.AbortIllegalModeTravel();
        current.LogTargetSummary(ActivityEnd.SwitchedToPot);
        Ledger?.Ended(current, ActivityEnd.SwitchedToPot);
        Activity = pot;
        Ledger?.Selected(pot, ActivitySource.SwitchedToPot);
        var kind = current is CriticalEncounter ? "緊急遭遇戰" : "F.A.T.E.";
        Svc.Log.Information($"[BOCCHI][非法模式] 前往{kind}「{current.GetName()}」途中出現魔法甕「{pot.GetName()}」（設定：魔法甕優先）：中止趕路，改去魔法甕；導航：{navigation}");
        return true;
    }

    /// <summary>
    /// 設定「魔法甕快出現時先到出生點等」：沒有可接的活動、推估的下一個魔法甕在出發窗內（<see cref="PotWaitRules.WantsWait" />）時，
    /// 改成前往它的出生點附近等。回 true＝這一幀開始等。只在框架執行緒呼叫。
    /// </summary>
    private bool TryStartPotWait(AutomatorModule module, Lifestream lifestream, VNavmesh vnav)
    {
        if (!fatesResolved)
        {
            fatesResolved = true;
            module.TryGetModule(out fates);
        }

        if (fates == null || !PotWaitRules.WantsWait(module.Config, fates.potCycle.Cycle, fates.Config.GetPotCyclePeriod(), DateTime.UtcNow, out var potId, out var remaining))
        {
            return false;
        }

        var wait = new PotWaitActivity(EventData.Fates[potId], lifestream, vnav, module, fates, DateTime.UtcNow + remaining);
        Activity = wait;
        wait.ReportDeparted(PotWaitRules.DepartureMinutes(module.Config));
        return true;
    }

    /// <summary>
    /// 等魔法甕途中（沒在戰鬥、不在 F.A.T.E.／緊急遭遇戰裡）照常選活動：選得到（魔法甕出現、窗外可接的 F.A.T.E.…）就中止等待改去那裡。
    /// 返回／傳送的詠唱與讀取中不打斷。只在框架執行緒呼叫。
    /// </summary>
    private bool TryLeaveWaitForActivity(AutomatorModule module, StateManagerModule states, PotWaitActivity wait, Lifestream lifestream, VNavmesh vnav)
    {
        if (states.GetState() != State.Idle)
        {
            return false;
        }

        var now = Environment.TickCount64;
        if (now < nextLeaveWaitCheckAt)
        {
            return false;
        }

        nextLeaveWaitCheckAt = now + LeaveFateTravelCheckIntervalMs;
        if (IsCastingOrBusy() || IsLifestreamBusy(lifestream))
        {
            return false;
        }

        var next = SelectNext(module, lifestream, vnav, out var source);
        if (next == null)
        {
            return false;
        }

        var navigation = module.Plugin.Automation.AbortIllegalModeTravel();
        wait.ReportLeft(next, navigation);
        Activity = next;
        Ledger?.Selected(next, source);
        return true;
    }

    /// <summary>
    /// 設定「前往 F.A.T.E. 途中改去緊急遭遇戰」：還在趕路（不在 F.A.T.E. 裡、沒在戰鬥）時，有勾選參加的緊急遭遇戰正在報名、
    /// 剩餘報名秒數不超過門檻，就中止 F.A.T.E. 的趕路鏈並換成那一場；之後走非法模式原本前往緊急遭遇戰的路徑。
    /// 返回／傳送的詠唱與讀取中不打斷。魔法甕優先時，前往魔法甕途中、推估魔法甕快出現時都不改（各只寫一次記錄）。
    /// </summary>
    private bool TryLeaveFateTravelForCriticalEncounter(AutomatorModule module, StateManagerModule states, FateActivity fate, Lifestream lifestream, VNavmesh vnav)
    {
        var config = module.Config;
        if (!config.ShouldLeaveFateTravelForCriticalEncounter
            || fate.state is not (ActivityState.Idle or ActivityState.Pathfinding)
            || states.GetState() != State.Idle)
        {
            return false;
        }

        var now = Environment.TickCount64;
        if (now < nextLeaveFateTravelCheckAt)
        {
            return false;
        }

        nextLeaveFateTravelCheckAt = now + LeaveFateTravelCheckIntervalMs;
        if (IsCastingOrBusy() || IsLifestreamBusy(lifestream))
        {
            return false;
        }

        if (!criticalEncountersResolved)
        {
            criticalEncountersResolved = true;
            module.TryGetModule(out criticalEncounters);
        }

        if (criticalEncounters == null)
        {
            return false;
        }

        var threshold = config.LeaveFateTravelForCriticalEncounterSeconds;
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var keepPot = config.ShouldPrioritizePots && PotCycle.IsPot(fate.data.Id);
        var potDue = TryGetPotDueSoon(module, out var potId, out var potRemaining) && !keepPot;
        foreach (var encounter in criticalEncounters.CriticalEncounters.Values)
        {
            if (encounter.State != DynamicEventState.Register
                || !config.TryGetCriticalEncounterEnabled(encounter.DynamicEventId, out var on) || !on
                || IsSkipped(encounter)
                || !EventData.CriticalEncounters.TryGetValue(encounter.DynamicEventId, out var data)
                || !CriticalEncounterTravelRules.ShouldLeaveFateTravel(threshold, encounter.StartTimestamp, nowUnix, out var remaining))
            {
                continue;
            }

            var left = remaining >= 0 ? $"報名剩 {remaining} 秒" : "讀不到剩餘報名時間";
            if (keepPot)
            {
                if (!ReferenceEquals(potKeptReportedFor, fate))
                {
                    potKeptReportedFor = fate;
                    Svc.Log.Information($"[BOCCHI][非法模式] 前往魔法甕「{fate.GetName()}」途中，緊急遭遇戰「{encounter.Name}」{left}：設定魔法甕優先，不改去緊急遭遇戰");
                }

                return false;
            }

            if (potDue)
            {
                ReportPotSkip(module, encounter, fate, potId, potRemaining);
                return false;
            }

            var navigation = module.Plugin.Automation.AbortIllegalModeTravel();
            fate.LogTargetSummary(ActivityEnd.SwitchedToCriticalEncounter);
            Ledger?.Ended(fate, ActivityEnd.SwitchedToCriticalEncounter);
            Activity = new CriticalEncounter(data, lifestream, vnav, module, criticalEncounters);
            Ledger?.Selected(Activity, ActivitySource.SwitchedToCriticalEncounter);
            Svc.Log.Information($"[BOCCHI][非法模式] 前往 F.A.T.E.「{fate.GetName()}」途中，緊急遭遇戰「{encounter.Name}」{left}（門檻 {threshold} 秒）：中止 F.A.T.E. 的趕路，改去緊急遭遇戰；導航：{navigation}");
            return true;
        }

        return false;
    }

    // 參加過的魔法甕結束後一兩秒財寶提示才到，EurekaHelper 收到才開始尋寶：先等它一下再選下一個活動。沒參加到不會有提示。
    private void ArmPotHuntGrace(Activity ended)
    {
        if (ended is not FateActivity || !PotCycle.IsPot(ended.data.Id) || ended.state is not (ActivityState.Participating or ActivityState.Done)
            || !potHuntIpc.TryRead(out _))
        {
            return;
        }

        potHunt.ArmGrace(Environment.TickCount64);
        nextPotHuntPollAt = 0;
        Svc.Log.Information($"[BOCCHI][魔法甕尋寶] 魔法甕「{ended.GetName()}」結束：先等 EurekaHelper 開始尋寶（最多 {PotHuntYield.StartGraceMs / 1000} 秒），這段時間不選下一個活動。");
    }

    /// <summary>
    /// EurekaHelper 在找魔法甕的財寶（或魔法甕剛結束、還在等它開始）時回 true：先不選下一個活動、不出發。
    /// 正在趕路（或待命返回）就中止自己的動作鏈，vnavmesh 只停 BOCCHI 自己的路線；結束後活動從目前位置接續。只在框架執行緒呼叫。
    /// </summary>
    private bool HoldForPotHunt(AutomatorModule module, StateManagerModule states)
    {
        var now = Environment.TickCount64;
        if (potHuntHolding && module.Config.YieldToManualMovement)
        {
            // 每拍讀按鍵：等完（含上限）接著出發時，返回前問得到你最近有沒有在操作（HandsOnRecent）。
            potHandsOn ??= new HandsOnProbe();
            potHandsOn.ReadInput(now);
        }

        if (now >= nextPotHuntPollAt)
        {
            nextPotHuntPollAt = now + PotHuntPollMs;
            var available = potHuntIpc.TryRead(out var active);
            potHuntHolding = potHunt.Step(now, available, active, out var ev, out var heldMs);
            ReportPotHunt(ev, heldMs);
            if (ev == PotHuntEvent.HuntEnded)
            {
                potChest.Arm(now);
                Svc.Log.Information($"[BOCCHI][財寶箱讓位] 魔法甕尋寶結束：先不選下一個活動、不出發，等你開挖出來的財寶箱（{PotChestSettle.NearbyM:F0} 米內還有財寶箱、"
                                    + $"或你在開箱／對話中就繼續等；連續 {PotChestSettle.QuietMs / 1000} 秒都沒有才繼續，看過財寶箱之後改等 {PotChestSettle.QuietAfterChestMs / 1000} 秒"
                                    + $"（等「第二處財寶」），最多等 {PotChestSettle.CapMs / 1000} 秒）。");
            }

            if (potHuntHolding)
            {
                potChest.Reset();
            }
            else if (potChest.IsArmed)
            {
                potHuntHolding = potChest.Step(now, PotChestBusy(), SecondTreasureNotice.Seen, SecondTreasureNotice.SeenAt, out var chestEvent, out var chestHeldMs);
                ReportPotChest(chestEvent, chestHeldMs);
            }

            if (!potHuntHolding)
            {
                potHuntTravelYielded = false;
            }
        }

        if (!potHuntHolding)
        {
            return false;
        }

        if (potHunt.IsHunting && !potHuntTravelYielded && IsChainActive && (Activity != null || IsIdleReturnRunning())
            && (states.GetState() is State.Idle or State.InCombat) && !Svc.Condition[ConditionFlag.BetweenAreas] && !Svc.Condition[ConditionFlag.BetweenAreas51])
        {
            potHuntTravelYielded = true;
            var navigation = module.Plugin.Automation.YieldIllegalModeTravel();
            var what = Activity == null ? "待命返回" : $"前往{Activity.KindLabel}「{Activity.GetName()}」";
            Svc.Log.Information($"[BOCCHI][魔法甕尋寶] {what}途中 EurekaHelper 在尋寶：中止自己的動作鏈，等它找完再接續；導航：{navigation}");
        }

        return true;
    }

    // 在開箱、對話一類的互動事件裡，或附近有可選取的魔法甕財寶箱；只在框架執行緒、只在這一幀讀物件表，不保存物件。
    internal static bool PotChestBusy()
    {
        var condition = Svc.Condition;
        if (condition[ConditionFlag.OccupiedInEvent] || condition[ConditionFlag.OccupiedInQuestEvent])
        {
            return true;
        }

        var player = Svc.Objects.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        var from = player.Position;
        foreach (var obj in Svc.Objects)
        {
            if (obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj && obj.IsTargetable && PotChestSettle.IsPotChest(obj.BaseId)
                && System.Numerics.Vector3.Distance(from, obj.Position) <= PotChestSettle.NearbyM)
            {
                return true;
            }
        }

        return false;
    }

    private void ReportPotChest(PotChestSettleEvent ev, long heldMs)
    {
        var seconds = LedgerRules.Seconds(heldMs);
        switch (ev)
        {
            case PotChestSettleEvent.Quiet:
                Svc.Log.Information($"[BOCCHI][財寶箱讓位] 附近沒有魔法甕財寶箱、也沒在開箱已 {PotChestSettle.QuietMs / 1000} 秒（共等了 {seconds} 秒，"
                                    + $"{(potChest.SawChest ? "期間看過財寶箱或開箱" : "期間沒看到財寶箱")}）：繼續非法模式。");
                break;
            case PotChestSettleEvent.CapReached:
                Svc.Log.Information($"[BOCCHI][財寶箱讓位] 等開財寶箱滿 {PotChestSettle.CapMs / 1000} 秒（附近還有財寶箱或還在開箱）：先繼續非法模式。");
                break;
            case PotChestSettleEvent.SecondTreasure:
                Svc.Log.Information($"[BOCCHI][財寶箱讓位] 收到「第二處財寶」（尋寶結束後 {seconds} 秒）：繼續不選活動、不出發，等 EurekaHelper 開始找第二處（最多 {PotChestSettle.SecondTreasureWaitMs / 1000} 秒）。");
                break;
            case PotChestSettleEvent.SecondTreasureExpired:
                Svc.Log.Information($"[BOCCHI][財寶箱讓位] 收到「第二處財寶」後 {PotChestSettle.SecondTreasureWaitMs / 1000} 秒 EurekaHelper 都沒開始找（共等了 {seconds} 秒）：繼續非法模式。");
                break;
        }
    }

    private static bool IsIdleReturnRunning()
    {
        return ChainManager.Queues.TryGetValue("OCH##main", out var main) && main.CurrentChain?.Name == nameof(ReturnChain);
    }

    private void ReportPotHunt(PotHuntEvent ev, long heldMs)
    {
        var seconds = LedgerRules.Seconds(heldMs);
        switch (ev)
        {
            case PotHuntEvent.HuntStarted:
                var next = Activity == null ? "目前沒有活動" : $"之後前往{Activity.KindLabel}「{Activity.GetName()}」";
                Svc.Log.Information($"[BOCCHI][魔法甕尋寶] EurekaHelper 開始找魔法甕的財寶（會自己移動）：先不選下一個活動、不出發，等它找完（最多 {PotHuntYield.HuntCapMs / 1000} 秒）；{next}。");
                break;
            case PotHuntEvent.HuntEnded:
                Svc.Log.Information($"[BOCCHI][魔法甕尋寶] EurekaHelper 尋寶結束（等了 {seconds} 秒）：繼續非法模式。");
                break;
            case PotHuntEvent.GraceExpired:
                Svc.Log.Information($"[BOCCHI][魔法甕尋寶] 魔法甕結束後 {seconds} 秒內 EurekaHelper 沒有開始尋寶：照常選下一個活動。");
                break;
            case PotHuntEvent.CapReached:
                Svc.Log.Information($"[BOCCHI][魔法甕尋寶] 等 EurekaHelper 尋寶超過 {PotHuntYield.HuntCapMs / 1000} 秒：先繼續非法模式，它這一輪結束之前不再等。");
                break;
            case PotHuntEvent.Lost:
                Svc.Log.Information($"[BOCCHI][魔法甕尋寶] 等尋寶途中問不到 EurekaHelper（等了 {seconds} 秒）：不再等，繼續非法模式。");
                break;
        }
    }

    private static void ReportCombatHold(long heldMs)
    {
        var line = $"[BOCCHI][等脫離戰鬥] 秒={LedgerRules.Seconds(heldMs)}（沒有進行中的活動時，戰鬥中不選下一個活動；脫離戰鬥後才選）";
        if (heldMs >= CombatHoldReportMs)
        {
            Svc.Log.Information(line);
        }
        else
        {
            Svc.Log.Debug(line);
        }
    }

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

    internal static bool IsCastingOrBusy()
    {
        var condition = Svc.Condition;
        return condition[ConditionFlag.Casting]
               || condition[ConditionFlag.Casting87]
               || condition[ConditionFlag.BetweenAreas]
               || condition[ConditionFlag.BetweenAreas51]
               || condition[ConditionFlag.Occupied]
               || condition[ConditionFlag.Occupied39]
               || condition[ConditionFlag.OccupiedInEvent]
               || condition[ConditionFlag.OccupiedInCutSceneEvent]
               || condition[ConditionFlag.Jumping]
               || condition[ConditionFlag.Jumping61];
    }

    internal static bool IsLifestreamBusy(Lifestream lifestream)
    {
        try
        {
            return lifestream.IsBusy();
        }
        catch (Exception)
        {
            // 問不到就當成忙：寧可這次不換，也不要在傳送途中中止。
            return true;
        }
    }

    public void Refresh()
    {
        Refresh(ActivityEnd.ManualStop);
    }

    /// <param name="end">活動帳記的結束原因（停止、換區、倒地、卸載…）。</param>
    public void Refresh(ActivityEnd end)
    {
        if (Activity != null)
        {
            (Activity as PotWaitActivity)?.ReportStopped(end);
            Activity.LogTargetSummary(end);
            Ledger?.Ended(Activity, end);
        }

        Ledger?.Stopped(end);
        Activity = null;
        idleTime = 0;
        jobSwitchHoldSince = 0;
        jobSwitchHoldSkipUntil = 0;
        nextLeaveFateTravelCheckAt = 0;
        nextPotTravelCheckAt = 0;
        nextLeaveWaitCheckAt = 0;
        resumeNoteKey = 0;
        ceSkipNoteKey = 0;
        potKeptReportedFor = null;
        potSkipReported.Clear();
        potFateSkipReported.Clear();
        potHunt.Reset();
        potChest.Reset();
        potHuntHolding = false;
        potHuntTravelYielded = false;
        nextPotHuntPollAt = 0;
        combatHold.Reset();
        selfRepair.Reset("非法模式停止或換區");
        silverChests.Reset("非法模式停止或換區");
    }

    /// <summary>模組卸載時呼叫：拆掉自動修理的確認框監聽。</summary>
    public void Dispose()
    {
        selfRepair.Dispose();
    }
}
