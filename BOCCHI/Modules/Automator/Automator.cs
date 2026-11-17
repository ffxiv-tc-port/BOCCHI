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

    private long nextForkedTowerCheckAt;

    // 「兩歧塔開放報名、但正在參加別的活動」已寫過記錄的那一輪（報名截止的 Unix 秒）；0＝沒有。
    private int forkedTowerBusyReportedFor;

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
            // 活動消失是自動化自己的結束：只停 BOCCHI 為它送的路線，別的插件或你自己送的不停。
            Activity.StopOwnMove("[BOCCHI][非法模式]", "活動消失、停下前往", null);
            (Activity as PotWaitActivity)?.ReportEnded();
            (Activity as ForkedTowerWaitActivity)?.ReportEnded();
            Activity.LogTargetSummary(ActivityEnd.Vanished);
            Ledger?.Ended(Activity, ActivityEnd.Vanished);
            ArmPotHuntGrace(Activity);
            Activity = null;
        }

        if ((Activity == null || Activity.state is ActivityState.Idle or ActivityState.Pathfinding or ActivityState.WaitingForPot) && HoldForPotHunt(module, states))
        {
            return;
        }

        if (TrySwitchToForkedTower(module, states, lifestream, vnav))
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
            (Activity as ForkedTowerWaitActivity)?.ReportStopped(end);
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
        nextForkedTowerCheckAt = 0;
        forkedTowerBusyReportedFor = 0;
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
