using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BOCCHI.Chains;
using BOCCHI.Data;
using BOCCHI.Ledger;
using BOCCHI.Modules.Automator;
using BOCCHI.Modules.Carrots;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.MobFarmer;
using BOCCHI.Modules.PhantomJobs;
using BOCCHI.Modules.StateManager;
using BOCCHI.Modules.Teleporter;
using BOCCHI.Modules.Treasure;
using BOCCHI.Pathfinding;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Ocelot;
using Ocelot.Chain;
using Ocelot.IPC;
using Ocelot.Modules;

namespace BOCCHI.Automation;

// 本檔契約：開始／停止只在 framework 執行緒執行；其他執行緒（IPC 呼叫端）只能 RequestStopAll 排隊，下一幀停。開始一律手動（按鈕、指令、設定視窗勾選）；載入插件、進島、登入都不會開始。
// 自動停止：任何區域切換、角色無法戰鬥、登出／斷線、插件卸載；停止時一併中止動作鏈、導航與自己開的戰鬥 AI。
// 輔助職業技能清單跟著非法模式／自動打怪的執行狀態每幀同步：不論從哪條路徑停下，下一幀就交還 WrathCombo 的租約。
// 寶箱／幸運胡蘿蔔巡迴的進度：暫停、全部停止（含 IPC 急停）、自動停止都保留成「暫停」，但一律不會自己續跑；單項停止、從頭開始、放棄才丟掉；換區與登出讓保留的進度失效。暫停中不算在跑，不佔移動自動化的互斥。
// 停止、全部停止、暫停、非法模式讓 EurekaHelper 尋寶中止了 BOCCHI 自己詠唱中的「返回」時，詠唱一起取消（只取消返回，不碰別的詠唱）。
public sealed class AutomationControl : IDisposable
{
    private const string MainQueue = "OCH##main";

    private const string WatcherSuffix = "##watcher";

    // 停止後這段時間內 vnavmesh 又走起來、而且走的是 BOCCHI 送過的目的地就再停一次：停止前送出的尋路可能在背景算完才開走。
    // 別的插件或使用者在這段時間送的移動（例：EurekaHelper 的 /vnav moveflag）不停。
    private const long NavigationGuardMs = 3000;

    private readonly AutomatorModule? automator;

    private readonly MobFarmerModule? mobFarmer;

    private readonly TreasureModule? treasure;

    private readonly CarrotsModule? carrots;

    private readonly TeleporterModule? teleporter;

    private readonly PhantomJobsModule? phantomJobs;

    private readonly StateManagerModule? stateManager;

    private readonly CriticalEncountersModule? criticalEncounters;

    // 因為倒地而全部停止的時間與位置；0＝沒有在等復活。
    private long downedAt;

    private System.Numerics.Vector3 downedPosition;

    private readonly DeathRestore deathRestore = new();

    // 記下清單時組好的名稱與主視窗那一列的文字：主視窗每幀要畫，不每幀組字串。
    private string deathRestoreNames = string.Empty;

    private string deathRestoreLabel = string.Empty;

    private readonly VNavmesh? vnav;

    private readonly Lifestream? lifestream;

    private readonly bool[] runningLastFrame = new bool[AutomationKindExtensions.All.Length];

    // 其他執行緒排進來的停止要求（呼叫端給的署名）；null＝沒有。
    private string? pendingExternalStop;

    private bool illegalSeenEnabled;

    private long navigationGuardUntil;

    private bool navigationGuardFired;

    // 守衛只停終點落在這些目的地附近的路線：停下導航那一刻 BOCCHI 自己送過的。
    private readonly OwnMoveDestinations guardDestinations = new();

    // true＝非法模式還開著、只是讓 EurekaHelper 尋寶：讓位結束（或停止）就收掉守衛。
    private bool guardForPotHunt;

    // 倒地那一刻有沒有自動化在跑（復活那一行用）。
    private bool downedWhileRunning;

    // 「插件有沒有載入」要走反射，主視窗每幀都在問，所以一秒只真的查一次。
    private long pluginCheckedAt;

    private bool pluginsChecked;

    private bool vnavLoaded;

    private bool lifestreamLoaded;

    private bool stopping;

    private bool disposed;

    public AutomationControl(Plugin plugin)
    {
        automator = Find<AutomatorModule>(plugin);
        mobFarmer = Find<MobFarmerModule>(plugin);
        treasure = Find<TreasureModule>(plugin);
        carrots = Find<CarrotsModule>(plugin);
        teleporter = Find<TeleporterModule>(plugin);
        phantomJobs = Find<PhantomJobsModule>(plugin);
        stateManager = Find<StateManagerModule>(plugin);
        criticalEncounters = Find<CriticalEncountersModule>(plugin);
        vnav = FindIpc<VNavmesh>(plugin);
        lifestream = FindIpc<Lifestream>(plugin);
        PhantomActions = new PhantomActionAllowList(plugin, FindIpc<WrathPhantomIpc>(plugin));

        illegalSeenEnabled = automator?.Config.Enabled == true;

        Svc.Framework.Update += OnFrameworkUpdate;
        Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
        Svc.ClientState.Logout += OnLogout;

        _ = new AutomationIpc(this);
    }

    /// <summary>輔助職業技能清單（總開關打開時，BOCCHI 執行期間只讓 WrathCombo 使用勾選的技能）。</summary>
    public PhantomActionAllowList PhantomActions { get; }

    /// <summary>主視窗控制列要畫非法模式的目前活動；模組沒載入時是 null。</summary>
    public AutomatorModule? Automator
    {
        get => automator;
    }

    public bool IsRunning(AutomationKind kind)
    {
        return kind switch
        {
            AutomationKind.IllegalMode => automator?.Config.Enabled == true,
            AutomationKind.MobFarmer => mobFarmer?.Farmer.Running == true,
            AutomationKind.TreasureHunt => treasure?.Hunter?.IsRunning == true,
            AutomationKind.CarrotHunt => carrots?.Hunter?.IsRunning == true,
            AutomationKind.ReturnAfterEvents => teleporter?.teleporter.ReturnArmed == true,
            AutomationKind.PhantomJobLeveling => phantomJobs?.Leveling.IsRunning == true,
            _ => false,
        };
    }

    public bool IsAnyRunning()
    {
        foreach (var kind in AutomationKindExtensions.All)
        {
            if (IsRunning(kind))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>主視窗要不要畫恢復列：「死亡後提示恢復自動化」開著，而且有倒地時停下的清單。</summary>
    public bool HasDeathRestoreOffer
    {
        get => DeathRestoreEnabled && deathRestore.HasSnapshot;
    }

    /// <summary>主視窗恢復列的文字（含倒地時停下的項目）；沒有清單時是空字串。</summary>
    public string DeathRestoreLabel
    {
        get => deathRestoreLabel;
    }

    private bool DeathRestoreEnabled
    {
        get => automator?.Config.PromptRestoreAfterDeath == true;
    }

    private bool IsAnyRunningOrWasRunning()
    {
        foreach (var kind in AutomationKindExtensions.All)
        {
            if (IsRunning(kind) || runningLastFrame[(int)kind])
            {
                return true;
            }
        }

        return false;
    }

    public static string GetName(AutomationKind kind)
    {
        return I18N.T(kind.NameKey());
    }

    /// <summary>現在能不能開始；不能時 <paramref name="whyNot"/> 是給使用者看的原因。</summary>
    public bool CanStart(AutomationKind kind, out string whyNot)
    {
        if (IsRunning(kind))
        {
            whyNot = I18N.T("automation.blocked.already_running");
            return false;
        }

        return CanStartIgnoringRunning(kind, out whyNot);
    }

    /// <param name="announce">成功時要不要在聊天視窗說一聲；開不起來一律會說原因。</param>
    public bool Start(AutomationKind kind, string source, bool announce)
    {
        if (!CanStart(kind, out var whyNot))
        {
            Svc.Log.Information($"[BOCCHI][自動化] 沒有開始「{GetName(kind)}」（{source}）：{whyNot}");
            Svc.Chat.Print(I18N.T("automation.messages.cannot_start", new Dictionary<string, string> { ["name"] = GetName(kind), ["reason"] = whyNot }));
            return false;
        }

        if (GetHunter(kind) is { Progress.HasSaved: true } saved)
        {
            LogAbandon(kind, saved, I18N.T("automation.source.start_fresh"));
        }

        switch (kind)
        {
            case AutomationKind.IllegalMode:
                automator!.EnableIllegalMode();
                illegalSeenEnabled = true;
                break;
            case AutomationKind.MobFarmer:
                mobFarmer!.Farmer.Start();
                break;
            case AutomationKind.TreasureHunt:
                treasure!.Hunter!.Start();
                break;
            case AutomationKind.CarrotHunt:
                carrots!.Hunter!.Start();
                break;
            case AutomationKind.ReturnAfterEvents:
                teleporter!.teleporter.ArmReturn();
                break;
            case AutomationKind.PhantomJobLeveling:
                phantomJobs!.Leveling.Start();
                break;
        }

        runningLastFrame[(int)kind] = true;
        Svc.Log.Information($"[BOCCHI][自動化] 開始「{GetName(kind)}」（{source}）");
        InvalidateDeathRestoreOnManualStart(kind);
        if (kind == AutomationKind.IllegalMode)
        {
            ApplyQuickRestartSkip(source);
        }

        // 非法模式開始時本來就會在聊天視窗說一聲，不重複。
        if (announce && kind != AutomationKind.IllegalMode)
        {
            Svc.Chat.Print(I18N.T("automation.messages.started", new Dictionary<string, string> { ["name"] = GetName(kind) }));
        }

        return true;
    }

    public void Toggle(AutomationKind kind, string source, bool announce)
    {
        if (IsRunning(kind))
        {
            Stop(kind, source, announce);
            return;
        }

        Start(kind, source, announce);
    }

    /// <summary>停掉一項；沒在跑時只做收尾不報告。回傳它原本是否在跑。</summary>
    /// <param name="keepProgress">巡迴的進度保留成暫停（自動停止用）；false＝連保留的進度一起丟掉（使用者明確停止這一項）。</param>
    public bool Stop(AutomationKind kind, string reason, bool announce, bool keepProgress = false)
    {
        return StopOne(kind, reason, announce, IsRunning(kind) || runningLastFrame[(int)kind], keepProgress);
    }

    /// <summary>寶箱／幸運胡蘿蔔巡迴；其他項目是 null。</summary>
    public Hunter? GetHunter(AutomationKind kind)
    {
        return kind switch
        {
            AutomationKind.TreasureHunt => treasure?.Hunter,
            AutomationKind.CarrotHunt => carrots?.Hunter,
            _ => null,
        };
    }

    /// <summary>暫停巡迴：停下移動與動作鏈（與停止相同的範圍），保留進度。只在 framework 執行緒呼叫；回傳是否真的暫停了。</summary>
    public bool Pause(AutomationKind kind, string source, bool announce)
    {
        var hunter = GetHunter(kind);
        if (stopping || hunter == null || !hunter.IsRunning)
        {
            return false;
        }

        stopping = true;
        try
        {
            var returnCast = ReturnChain.OwnCastInFlight;
            ResetState(kind, true, new StopContext(ActivityEnd.ManualStop, HuntEnd.Paused, source, false));
            runningLastFrame[(int)kind] = false;
            var navigation = AbortForMovementKind(kind);
            CancelAbortedReturnCast(returnCast, $"停止（{source}）");
            Svc.Log.Information($"[BOCCHI][巡迴進度] 暫停「{GetName(kind)}」（{source}）：已跑 {hunter.ProgressText} 點，保留進度；導航：{navigation}");
            if (announce)
            {
                Svc.Chat.Print(I18N.T("automation.messages.paused", new Dictionary<string, string> { ["name"] = GetName(kind), ["progress"] = hunter.ProgressText }));
            }

            return true;
        }
        finally
        {
            stopping = false;
        }
    }

    /// <summary>現在能不能續跑；不能時 <paramref name="whyNot"/> 是給使用者看的原因。會配置字串，畫面上只在要顯示原因時呼叫。</summary>
    public bool CanResume(AutomationKind kind, out string whyNot)
    {
        var hunter = GetHunter(kind);
        if (hunter == null)
        {
            return kind.SupportsPause() ? Unavailable(out whyNot) : NotPausable(kind, out whyNot);
        }

        if (!hunter.CanResume(out var invalid))
        {
            whyNot = ResumeBlockedText(hunter, invalid);
            return false;
        }

        return CanStart(kind, out whyNot);
    }

    /// <summary>續跑：從角色目前位置重新規劃還沒跑過的點。檢查與開始相同（互斥、在島上、插件），一律手動。</summary>
    public bool Resume(AutomationKind kind, string source, bool announce)
    {
        if (!CanResume(kind, out var whyNot))
        {
            Svc.Log.Information($"[BOCCHI][巡迴進度] 沒有續跑「{GetName(kind)}」（{source}）：{whyNot}");
            Svc.Chat.Print(I18N.T("automation.messages.cannot_resume", new Dictionary<string, string> { ["name"] = GetName(kind), ["reason"] = whyNot }));
            return false;
        }

        var hunter = GetHunter(kind)!;
        if (!hunter.Resume())
        {
            return false;
        }

        runningLastFrame[(int)kind] = true;
        Svc.Log.Information($"[BOCCHI][巡迴進度] 續跑「{GetName(kind)}」（{source}）：已跑 {hunter.ProgressText} 點，從目前位置重新規劃剩下的點");
        InvalidateDeathRestoreOnManualStart(kind);
        if (announce)
        {
            Svc.Chat.Print(I18N.T("automation.messages.resumed", new Dictionary<string, string> { ["name"] = GetName(kind), ["progress"] = hunter.ProgressText }));
        }

        return true;
    }

    /// <summary>
    /// 恢復倒地時停下的那幾項：巡迴走續跑、其餘走開始，檢查與互斥照舊。只在 framework 執行緒、使用者按下時呼叫。
    /// 清單按下就取走（開不起來的那幾項由開始／續跑自己說原因）；無法戰鬥時不取走。回傳是否至少恢復了一項。
    /// </summary>
    public bool RestoreAfterDeath(string source, bool announce)
    {
        if (!DeathRestoreEnabled || !deathRestore.HasSnapshot)
        {
            var why = DeathRestoreEnabled
                ? I18N.T("automation.messages.nothing_to_restore")
                : I18N.T("automation.messages.restore_option_off", new Dictionary<string, string> { ["option"] = I18N.T("modules.automator.config.prompt_restore_after_death.label") });
            Svc.Log.Information($"[BOCCHI][倒地恢復] 沒有恢復（{source}）：{(DeathRestoreEnabled ? "沒有倒地時停下的清單" : "選項沒開")}");
            Svc.Chat.Print(why);
            return false;
        }

        if (Svc.Objects.LocalPlayer?.IsDead == true)
        {
            Svc.Log.Information($"[BOCCHI][倒地恢復] 沒有恢復（{source}）：還在無法戰鬥狀態，清單保留");
            Svc.Chat.Print(I18N.T("automation.messages.cannot_restore", new Dictionary<string, string> { ["reason"] = I18N.T("automation.blocked.dead") }));
            return false;
        }

        var names = deathRestoreNames;
        Span<bool> kinds = stackalloc bool[AutomationKindExtensions.All.Length];
        deathRestore.Take(kinds);
        ClearDeathRestoreText();

        var label = I18N.T("automation.source.death_restore", new Dictionary<string, string> { ["source"] = source });
        var restored = new List<string>();
        var failed = new List<string>();
        foreach (var kind in AutomationKindExtensions.All)
        {
            if (!kinds[(int)kind])
            {
                continue;
            }

            var ok = kind.SupportsPause() ? Resume(kind, label, announce) : Start(kind, label, announce);
            (ok ? restored : failed).Add(GetName(kind));
        }

        Svc.Log.Information($"[BOCCHI][倒地恢復] 恢復（{source}）：倒地時停下「{names}」；已恢復「{string.Join("、", restored)}」；沒恢復「{string.Join("、", failed)}」");
        return restored.Count > 0;
    }

    /// <summary>放棄暫停中（或已失效）的進度；正在跑的不動（那要用停止）。回傳是否真的丟掉了什麼。</summary>
    public bool AbandonProgress(AutomationKind kind, string source, bool announce)
    {
        var hunter = GetHunter(kind);
        if (hunter is not { IsRunning: false, Progress.HasSaved: true })
        {
            return false;
        }

        var progress = hunter.ProgressText;
        LogAbandon(kind, hunter, source);
        hunter.ResetRun(false);
        if (announce)
        {
            Svc.Chat.Print(I18N.T("automation.messages.progress_abandoned", new Dictionary<string, string> { ["name"] = GetName(kind), ["progress"] = progress }));
        }

        return true;
    }

    /// <summary>不能續跑的原因（給說明框與聊天訊息）。</summary>
    public static string ResumeBlockedText(Hunter hunter, HuntProgressInvalidation why)
    {
        return why switch
        {
            HuntProgressInvalidation.ZoneChanged => I18N.T("automation.resume_blocked.zone_changed"),
            HuntProgressInvalidation.LoggedOut => I18N.T("automation.resume_blocked.logged_out"),
            HuntProgressInvalidation.Expired => I18N.T("automation.resume_blocked.expired", new Dictionary<string, string> { ["minutes"] = (hunter.ResumeExpiryMs / 60_000).ToString() }),
            _ => hunter.IsRunning ? I18N.T("automation.blocked.already_running") : I18N.T("automation.resume_blocked.none"),
        };
    }

    /// <summary>全部停止。只能在 framework 執行緒呼叫；其他執行緒用 <see cref="RequestStopAll"/>。</summary>
    /// <remarks>使用者或其他插件要求的全部停止（含急停）：倒地時停下的清單一起作廢。</remarks>
    public void StopAll(string reason, bool announce)
    {
        InvalidateDeathRestore(reason);
        if (IsRunning(AutomationKind.IllegalMode))
        {
            automator?.automator.NoteManualStop();
        }

        StopAll(reason, announce, true, ActivityEnd.ManualStop);
        criticalEncounters?.Farm.Disarm(CeFarmStop.StopAll, reason);
    }

    // reportKept=false：呼叫端接著就讓巡迴進度失效（換區、登出），不要先說「已保留」。
    private void StopAll(string reason, bool announce, bool reportKept, ActivityEnd end)
    {
        if (stopping)
        {
            return;
        }

        stopping = true;
        try
        {
            var wasRunning = AutomationKindExtensions.All.Where(k => IsRunning(k) || runningLastFrame[(int)k]).ToList();
            var hadChains = AnyChainActive();
            var returnCast = ReturnChain.OwnCastInFlight;

            var context = new StopContext(end, HuntEnd.AutoStopped, reason, !reportKept);
            foreach (var kind in AutomationKindExtensions.All)
            {
                ResetState(kind, true, context);
            }

            if (reportKept)
            {
                foreach (var kind in wasRunning)
                {
                    LogKept(kind, reason, announce);
                }
            }

            illegalSeenEnabled = false;
            Array.Clear(runningLastFrame);

            // 包含 RetryChainFactory 另開的 ##watcher 佇列：只中止外層的話，正在跑的那一次重試會繼續施放、繼續走。
            ChainManager.AbortAll();
            teleporter?.CancelReturnPrompt();

            var navigation = wasRunning.Count > 0 || hadChains ? StopNavigation() : "沒有要停的";
            CancelAbortedReturnCast(returnCast, $"停止（{reason}）");

            var names = wasRunning.Count > 0 ? string.Join("、", wasRunning.Select(GetName)) : "（沒有正在跑的自動化）";
            Svc.Log.Information($"[BOCCHI][自動化] 全部停止（{reason}）：{names}；動作鏈{(hadChains ? "已中止" : "原本就沒有")}；導航：{navigation}");

            if (announce && wasRunning.Count > 0)
            {
                Svc.Chat.Print(I18N.T("automation.messages.stopped", new Dictionary<string, string> { ["names"] = names, ["reason"] = reason }));
            }
        }
        finally
        {
            stopping = false;
        }
    }

    /// <summary>任何執行緒都可以呼叫。在 framework 執行緒上當場停；否則排到下一幀。</summary>
    public void RequestStopAll(string? source)
    {
        if (disposed)
        {
            return;
        }

        var label = string.IsNullOrWhiteSpace(source) ? "?" : source.Trim();
        if (Svc.Framework.IsInFrameworkUpdateThread)
        {
            StopAll(ExternalReason(label), true);
            return;
        }

        Interlocked.Exchange(ref pendingExternalStop, label);
    }

    /// <summary>
    /// 非法模式自己換目標時用：中止正在跑的動作鏈（含 ##watcher 佇列）並停下導航，與停止非法模式時收的範圍相同，
    /// 但不改任何自動化的執行狀態。只在 framework 執行緒呼叫；回傳給 log 用的導航處理結果。
    /// </summary>
    public string AbortIllegalModeTravel()
    {
        return AbortForMovementKind(AutomationKind.IllegalMode);
    }

    /// <summary>換一場時用：收的範圍與停止非法模式相同（含 BOCCHI 自己詠唱中的「返回」），但不改任何自動化的執行狀態。只在 framework 執行緒呼叫。</summary>
    public string AbortIllegalModeTravelForSkip()
    {
        var returnCast = ReturnChain.OwnCastInFlight;
        var navigation = AbortForMovementKind(AutomationKind.IllegalMode);
        CancelAbortedReturnCast(returnCast, "換一場");
        return navigation;
    }

    /// <summary>主視窗「換一場」鈕能不能按；不能時 <paramref name="whyNotKey"/> 是原因的翻譯鍵（不組字串，每幀可呼叫）。</summary>
    public bool CanSkipCurrent(out string whyNotKey)
    {
        if (automator == null || !IsRunning(AutomationKind.IllegalMode))
        {
            whyNotKey = "automation.skip.blocked_not_running";
            return false;
        }

        var current = automator.automator.Activity;
        if (current == null)
        {
            whyNotKey = "automation.skip.blocked_no_activity";
            return false;
        }

        if (!global::BOCCHI.Modules.Automator.Automator.TryGetInstance(current, out _))
        {
            whyNotKey = "automation.skip.blocked_pot_wait";
            return false;
        }

        whyNotKey = string.Empty;
        return true;
    }

    /// <summary>
    /// 換一場（主視窗按鈕、/bocchi skip）：略過非法模式目前選定的那一場、馬上改挑下一場。非法模式不停，輔助職業技能清單（WrathCombo 的租約）不交還。
    /// 只在 framework 執行緒呼叫；回傳是否真的略過了。成功時一律在聊天視窗說明接下來會怎樣；沒略過時只有 <paramref name="announce"/> 才說。
    /// </summary>
    public bool SkipCurrent(string source, bool announce)
    {
        if (!CanSkipCurrent(out var whyNotKey) && whyNotKey == "automation.skip.blocked_not_running")
        {
            Svc.Log.Information($"[BOCCHI][換一場] 來源={source} 沒有略過：非法模式沒有在執行");
            if (announce)
            {
                Svc.Chat.Print(I18N.T("automation.messages.skip_nothing", new Dictionary<string, string> { ["reason"] = I18N.T(whyNotKey) }));
            }

            return false;
        }

        var outcome = automator!.automator.SkipCurrent(automator, source, out var name, out var place);
        if (outcome != SkipOutcome.Skipped)
        {
            if (announce)
            {
                var key = outcome == SkipOutcome.NoActivity ? "automation.skip.blocked_no_activity" : "automation.skip.blocked_pot_wait";
                Svc.Chat.Print(I18N.T("automation.messages.skip_nothing", new Dictionary<string, string> { ["reason"] = I18N.T(key) }));
            }

            return false;
        }

        var message = place switch
        {
            SkipPlace.InsideFate => "automation.messages.skip_done_in_fate",
            SkipPlace.InsideCriticalEncounter => "automation.messages.skip_done_in_ce",
            SkipPlace.InCombat => "automation.messages.skip_done_in_combat",
            _ => "automation.messages.skip_done",
        };
        Svc.Chat.Print(I18N.T(message, new Dictionary<string, string> { ["name"] = name }));
        return true;
    }

    /// <summary>本場略過的場數（主視窗每幀讀）。</summary>
    public int SkipCount
    {
        get => automator?.automator.SkipCount ?? 0;
    }

    /// <summary>本場略過的名稱，一行一場（說明框用；會配置，只在要顯示時呼叫）。</summary>
    public string SkippedNames()
    {
        if (automator == null || automator.automator.SkipCount == 0)
        {
            return string.Empty;
        }

        var skips = automator.automator.Skips;
        var names = new List<string>(skips.Count);
        for (var i = 0; i < skips.Count; i++)
        {
            names.Add($"・{skips.NameAt(i)}");
        }

        return string.Join("\n", names);
    }

    /// <summary>使用者清空本場略過（主視窗、/bocchi skip clear）：之後那幾場又可以被選到。只在 framework 執行緒呼叫。</summary>
    public int ClearSkips(string source, bool announce)
    {
        var count = automator?.automator.ClearSkips(I18N.T("automation.skip.cleared_by", new Dictionary<string, string> { ["source"] = source })) ?? 0;
        if (announce)
        {
            Svc.Chat.Print(I18N.T("automation.messages.skips_cleared", new Dictionary<string, string> { ["count"] = count.ToString() }));
        }

        return count;
    }

    // 非法模式開始（已成功）時：停止後很快又開始＝換一場，停止當下選定的那一場加進本場略過（設定可關）。
    private void ApplyQuickRestartSkip(string source)
    {
        if (automator?.automator.ApplyQuickRestart(automator, source, out var elapsedMs) is not { } name)
        {
            return;
        }

        Svc.Chat.Print(I18N.T("automation.messages.skip_quick_restart", new Dictionary<string, string>
        {
            ["seconds"] = Math.Max(1, (elapsedMs + 999) / 1000).ToString(),
            ["name"] = name,
        }));
    }

    /// <summary>常用組合（非法模式設定裡勾選）包不包含這一項；非法模式模組沒載入時一項都不包含。</summary>
    public bool InCombo(AutomationKind kind)
    {
        var config = automator?.Config;
        return config != null && kind switch
        {
            AutomationKind.IllegalMode => config.ComboIllegalMode,
            AutomationKind.MobFarmer => config.ComboMobFarmer,
            AutomationKind.TreasureHunt => config.ComboTreasureHunt,
            AutomationKind.CarrotHunt => config.ComboCarrotHunt,
            AutomationKind.ReturnAfterEvents => config.ComboReturnAfterEvents,
            AutomationKind.PhantomJobLeveling => config.ComboPhantomJobLeveling,
            _ => false,
        };
    }

    /// <summary>常用組合鈕現在的狀態；每幀可呼叫（不組原因字串，要說明時另外問 <see cref="ComboWhyNot"/>）。</summary>
    public ComboStatus GetComboStatus()
    {
        if (automator == null)
        {
            return ComboStatus.Unavailable;
        }

        var count = 0;
        var movement = 0;
        var allRunning = true;
        var blocked = false;
        foreach (var kind in AutomationKindExtensions.All)
        {
            if (!InCombo(kind))
            {
                continue;
            }

            count++;
            if (kind.DrivesMovement())
            {
                movement++;
            }

            if (IsRunning(kind))
            {
                continue;
            }

            allRunning = false;
            blocked |= !CanStart(kind, out _);
        }

        if (count == 0)
        {
            return ComboStatus.Empty;
        }

        if (movement > 1)
        {
            return ComboStatus.Conflict;
        }

        if (allRunning)
        {
            return ComboStatus.Running;
        }

        return blocked ? ComboStatus.Blocked : ComboStatus.Ready;
    }

    /// <summary>組合裡的項目（短名，以「、」分隔）；會配置，只在要顯示時呼叫。</summary>
    public string ComboNames()
    {
        return string.Join("、", AutomationKindExtensions.All.Where(InCombo).Select(k => I18N.T(k.ShortNameKey())));
    }

    /// <summary>組合鈕不能按的原因（給說明框與聊天視窗）；會配置，只在要顯示時呼叫。</summary>
    public string ComboWhyNot(ComboStatus status)
    {
        switch (status)
        {
            case ComboStatus.Unavailable:
                return I18N.T("automation.blocked.module_unavailable");
            case ComboStatus.Empty:
                return I18N.T("automation.combo.empty");
            case ComboStatus.Conflict:
                var moving = AutomationKindExtensions.All.Where(k => InCombo(k) && k.DrivesMovement()).Select(k => I18N.T(k.ShortNameKey()));
                return I18N.T("automation.combo.conflict", new Dictionary<string, string> { ["names"] = string.Join("、", moving) });
            case ComboStatus.Blocked:
                var lines = new List<string>();
                foreach (var kind in AutomationKindExtensions.All)
                {
                    if (InCombo(kind) && !IsRunning(kind) && !CanStart(kind, out var why))
                    {
                        lines.Add(I18N.T("automation.combo.blocked_item", new Dictionary<string, string> { ["name"] = I18N.T(kind.ShortNameKey()), ["reason"] = why }));
                    }
                }

                return string.Join("\n", lines);
            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// 開始常用組合：組合裡還沒在跑的項目照一般的開始條件逐項開始（已在跑的不動）。有任何一項開不起來就一項都不開，並說原因。
    /// 一律手動（主視窗按鈕、/bocchi combo）；只在 framework 執行緒呼叫。回傳是否開始了至少一項。
    /// </summary>
    public bool StartCombo(string source, bool announce)
    {
        var status = GetComboStatus();
        var skipCount = SkipCount;
        if (status != ComboStatus.Ready)
        {
            var why = status == ComboStatus.Running ? I18N.T("automation.combo.all_running") : ComboWhyNot(status);
            Svc.Log.Information($"[BOCCHI][組合開始] 來源={source} 沒有開始（{status}）：{why.Replace("\n", "；")} 組合=「{ComboNames()}」 略過集合={skipCount}");
            Svc.Chat.Print(I18N.T("automation.messages.combo_cannot_start", new Dictionary<string, string> { ["reason"] = why.Replace("\n", " ") }));
            return false;
        }

        var label = I18N.T("automation.source.combo", new Dictionary<string, string> { ["source"] = source });
        var already = new List<string>();
        var started = new List<string>();
        var failed = new List<string>();
        foreach (var kind in AutomationKindExtensions.All)
        {
            if (!InCombo(kind))
            {
                continue;
            }

            var name = I18N.T(kind.ShortNameKey());
            if (IsRunning(kind))
            {
                already.Add(name);
                continue;
            }

            (Start(kind, label, false) ? started : failed).Add(name);
        }

        Svc.Log.Information($"[BOCCHI][組合開始] 來源={source} 組合=「{ComboNames()}」 開始=「{string.Join("、", started)}」 已在跑=「{string.Join("、", already)}」 沒開成=「{string.Join("、", failed)}」 略過集合={SkipCount}");
        if (announce && started.Count > 0)
        {
            Svc.Chat.Print(I18N.T("automation.messages.combo_started", new Dictionary<string, string> { ["names"] = string.Join("、", started) }));
        }

        return started.Count > 0;
    }

    /// <summary>停止常用組合：只停組合裡正在跑的項目，其他自動化照舊。只在 framework 執行緒呼叫；回傳是否停了至少一項。</summary>
    public bool StopCombo(string source, bool announce)
    {
        var label = I18N.T("automation.source.combo", new Dictionary<string, string> { ["source"] = source });
        var stopped = new List<string>();
        foreach (var kind in AutomationKindExtensions.All)
        {
            if (InCombo(kind) && IsRunning(kind) && Stop(kind, label, false))
            {
                stopped.Add(I18N.T(kind.ShortNameKey()));
            }
        }

        Svc.Log.Information($"[BOCCHI][組合停止] 來源={source} 組合=「{ComboNames()}」 停止=「{string.Join("、", stopped)}」");
        if (announce)
        {
            Svc.Chat.Print(stopped.Count > 0
                ? I18N.T("automation.messages.combo_stopped", new Dictionary<string, string> { ["names"] = string.Join("、", stopped) })
                : I18N.T("automation.messages.combo_nothing_running"));
        }

        return stopped.Count > 0;
    }

    /// <summary>組合裡的項目全部在跑時停止組合，否則開始組合。</summary>
    public void ToggleCombo(string source, bool announce)
    {
        if (GetComboStatus() == ComboStatus.Running)
        {
            StopCombo(source, announce);
            return;
        }

        StartCombo(source, announce);
    }

    /// <summary>
    /// 非法模式讓 EurekaHelper 的魔法甕尋寶移動時用：中止自己的動作鏈並作廢趕路登記，vnavmesh 只停 BOCCHI 自己送的路線
    /// （不取消排隊中的尋路：分不出是誰的）；之後 <see cref="NavigationGuardMs"/> 內 BOCCHI 先前送的尋路算完開走也再停。
    /// 中止到 BOCCHI 自己詠唱中的「返回」時詠唱一起取消（不然幾秒後被傳回營地，EurekaHelper 要從營地走回去）。
    /// 不改任何自動化的執行狀態。只在 framework 執行緒呼叫；回傳給 log 用的導航處理結果。
    /// </summary>
    public string YieldIllegalModeTravel()
    {
        var returnCast = ReturnChain.OwnCastInFlight;
        AbortQueues(id => id == MainQueue || id.EndsWith(WatcherSuffix, StringComparison.Ordinal));
        teleporter?.CancelReturnPrompt();
        CancelAbortedReturnCast(returnCast, "讓 EurekaHelper 尋寶");
        var vnavReady = vnav != null && vnav.IsReady();
        if (vnavReady)
        {
            guardDestinations.CaptureCurrent(TravelIntent.State);
        }

        OwnMoveDestinations.Recent.Clear();
        TravelIntent.Invalidate();

        var parts = new List<string>();
        if (!vnavReady)
        {
            parts.Add("vnavmesh 未載入");
        }
        else
        {
            var verdict = JudgeCurrentPath(guardDestinations, out var end);
            if (verdict is OwnPathVerdict.Own or OwnPathVerdict.Unknown)
            {
                parts.Add(TryInvoke("Path.Stop（BOCCHI 自己的路線）", () => vnav!.Stop()));
            }
            else
            {
                parts.Add(verdict == OwnPathVerdict.Idle ? "vnavmesh 沒在走" : $"vnavmesh 正在走別人的路線（終點 {FormatPoint(end)}），不停");
            }

            navigationGuardUntil = Environment.TickCount64 + NavigationGuardMs;
            navigationGuardFired = false;
            guardForPotHunt = true;
        }

        if (lifestream != null && lifestream.IsReady())
        {
            try
            {
                if (lifestream.IsBusy())
                {
                    parts.Add(TryInvoke("Lifestream.Abort", () => lifestream.Abort()));
                }
            }
            catch (Exception ex)
            {
                parts.Add($"Lifestream.IsBusy 失敗（{ex.GetType().Name}）");
            }
        }

        return string.Join("、", parts);
    }

    /// <summary>vnavmesh 現在走的路線是不是 <paramref name="own"/> 裡的目的地；<paramref name="end"/>＝路線終點（讀不到是 null）。</summary>
    private OwnPathVerdict JudgeCurrentPath(OwnMoveDestinations own, out System.Numerics.Vector3? end)
    {
        end = null;
        try
        {
            if (vnav == null || !vnav.IsRunning())
            {
                return OwnPathVerdict.Idle;
            }

            var waypoints = vnav.GetWaypoints();
            if (waypoints is { Count: > 0 })
            {
                end = waypoints[^1];
            }
        }
        catch (Exception)
        {
            end = null;
            return OwnPathVerdict.Unknown;
        }

        return own.Judge(true, end);
    }

    private static string FormatPoint(System.Numerics.Vector3? point)
    {
        return point is { } p ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"({p.X:F1}, {p.Y:F1}, {p.Z:F1})") : "?";
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Svc.ClientState.Logout -= OnLogout;
        PhantomActions.Dispose();

        if (!IsAnyRunning())
        {
            return;
        }

        // 卸載時動作鏈由 Ocelot 自己收；這裡只負責不讓 vnavmesh 帶著角色繼續走到上一個目的地。
        var context = new StopContext(ActivityEnd.Unloaded, HuntEnd.Unloaded, "插件卸載", false);
        foreach (var kind in AutomationKindExtensions.All)
        {
            ResetState(kind, true, context, !Svc.Framework.IsFrameworkUnloading);
        }

        Svc.Log.Information($"[BOCCHI][自動化] 插件卸載時仍有自動化在跑，已停止；導航：{StopNavigation()}");
    }

    private bool CanStartIgnoringRunning(AutomationKind kind, out string whyNot)
    {
        if (!Svc.ClientState.IsLoggedIn || !ZoneData.IsInOccultCrescent())
        {
            whyNot = I18N.T("automation.blocked.not_in_zone");
            return false;
        }

        if (Svc.Objects.LocalPlayer?.IsDead == true)
        {
            whyNot = I18N.T("automation.blocked.dead");
            return false;
        }

        if (kind.DrivesMovement())
        {
            foreach (var other in AutomationKindExtensions.All)
            {
                if (other != kind && other.DrivesMovement() && IsRunning(other))
                {
                    whyNot = I18N.T("automation.blocked.other_running", new Dictionary<string, string> { ["name"] = GetName(other) });
                    return false;
                }
            }
        }

        switch (kind)
        {
            case AutomationKind.IllegalMode:
                if (automator == null)
                {
                    return Unavailable(out whyNot);
                }

                return RequirePlugins(out whyNot, true, true);

            case AutomationKind.MobFarmer:
                if (mobFarmer == null)
                {
                    return Unavailable(out whyNot);
                }

                if (!mobFarmer.IsEnabled)
                {
                    whyNot = I18N.T("automation.blocked.module_disabled");
                    return false;
                }

                return RequirePlugins(out whyNot, true, false);

            case AutomationKind.TreasureHunt:
                if (treasure?.Hunter == null)
                {
                    return Unavailable(out whyNot);
                }

                if (!treasure.Config.ShouldEnableTreasureHunt)
                {
                    whyNot = I18N.T("automation.blocked.option_off", new Dictionary<string, string> { ["option"] = I18N.T("modules.treasure.config.show_hunt_button.label") });
                    return false;
                }

                return RequirePlugins(out whyNot, true, true);

            case AutomationKind.CarrotHunt:
                if (carrots?.Hunter == null)
                {
                    return Unavailable(out whyNot);
                }

                if (!carrots.Config.ShouldEnableCarrotHunt)
                {
                    whyNot = I18N.T("automation.blocked.option_off", new Dictionary<string, string> { ["option"] = I18N.T("modules.carrots.config.show_hunt_button.label") });
                    return false;
                }

                return RequirePlugins(out whyNot, true, true);

            case AutomationKind.ReturnAfterEvents:
                if (teleporter == null)
                {
                    return Unavailable(out whyNot);
                }

                if (!teleporter.Config.ReturnAfterFate && !teleporter.Config.ReturnAfterCriticalEncounter)
                {
                    whyNot = I18N.T("automation.blocked.return_not_configured", new Dictionary<string, string>
                    {
                        ["fate"] = I18N.T("modules.teleporter.config.return_after_fate.label"),
                        ["ce"] = I18N.T("modules.teleporter.config.return_after_critical_encounter.label"),
                    });
                    return false;
                }

                whyNot = string.Empty;
                return true;

            case AutomationKind.PhantomJobLeveling:
                if (phantomJobs == null)
                {
                    return Unavailable(out whyNot);
                }

                whyNot = string.Empty;
                return true;
        }

        return Unavailable(out whyNot);
    }

    private bool StopOne(AutomationKind kind, string reason, bool announce, bool wasRunning, bool keepProgress)
    {
        if (stopping)
        {
            return false;
        }

        stopping = true;
        try
        {
            if (!keepProgress && GetHunter(kind) is { Progress.State: not HuntProgressState.None } hunter)
            {
                LogAbandon(kind, hunter, reason);
            }

            // 單獨停止非法模式的都是使用者（見下方）：狀態重設之前記下選定的那一場。
            if (kind == AutomationKind.IllegalMode && wasRunning)
            {
                automator?.automator.NoteManualStop();
            }

            var returnCast = ReturnChain.OwnCastInFlight;
            ResetState(kind, keepProgress, new StopContext(ActivityEnd.ManualStop, keepProgress ? HuntEnd.AutoStopped : HuntEnd.Stopped, reason, false));
            runningLastFrame[(int)kind] = false;
            if (kind == AutomationKind.IllegalMode)
            {
                illegalSeenEnabled = false;
            }

            if (!wasRunning)
            {
                return false;
            }

            // 單獨停止非法模式的都是使用者（主視窗、指令、標題列、設定頁開關）；自動停止一律走 StopAll。
            if (kind == AutomationKind.IllegalMode)
            {
                teleporter?.teleporter.NoteIllegalModeStoppedByUser();
            }

            if (keepProgress)
            {
                LogKept(kind, reason, announce);
            }

            var navigation = kind switch
            {
                AutomationKind.ReturnAfterEvents => AbortForReturn(),
                AutomationKind.PhantomJobLeveling => "不涉及移動",
                _ => AbortForMovementKind(kind),
            };
            CancelAbortedReturnCast(returnCast, $"停止（{reason}）");
            Svc.Log.Information($"[BOCCHI][自動化] 停止「{GetName(kind)}」（{reason}）；導航：{navigation}");

            // 非法模式一向在開關時於聊天視窗說一聲，照舊。
            if (kind == AutomationKind.IllegalMode)
            {
                Svc.Chat.Print(I18N.T("modules.automator.messages.off"));
            }
            else if (announce)
            {
                Svc.Chat.Print(I18N.T("automation.messages.stopped", new Dictionary<string, string> { ["names"] = GetName(kind), ["reason"] = reason }));
            }

            return true;
        }
        finally
        {
            stopping = false;
        }
    }

    // 只重設該項自己的狀態（冪等）；動作鏈與導航由呼叫端收。keepProgress 只影響巡迴（見檔頭）；context 只影響記錄。
    private void ResetState(AutomationKind kind, bool keepProgress, StopContext context, bool sendCommands = true)
    {
        switch (kind)
        {
            case AutomationKind.IllegalMode:
                automator?.DisableIllegalMode(sendCommands, context.Activity);
                break;
            case AutomationKind.MobFarmer:
                mobFarmer?.Farmer.ResetRun();
                break;
            case AutomationKind.TreasureHunt:
                treasure?.Hunter?.ResetRun(keepProgress, context.Hunt, context.Reason, context.Invalidating);
                break;
            case AutomationKind.CarrotHunt:
                carrots?.Hunter?.ResetRun(keepProgress, context.Hunt, context.Reason, context.Invalidating);
                break;
            case AutomationKind.ReturnAfterEvents:
                teleporter?.teleporter.DisarmReturn();
                break;
            case AutomationKind.PhantomJobLeveling:
                phantomJobs?.Leveling.ResetRun();
                break;
        }
    }

    private string AbortForMovementKind(AutomationKind kind)
    {
        var own = kind switch
        {
            AutomationKind.MobFarmer => Farmer.QueueName,
            AutomationKind.TreasureHunt => treasure?.Hunter?.StepQueueName,
            AutomationKind.CarrotHunt => carrots?.Hunter?.StepQueueName,
            _ => null,
        };

        AbortQueues(id => id == MainQueue || id == own || id.EndsWith(WatcherSuffix, StringComparison.Ordinal));
        teleporter?.CancelReturnPrompt();
        return StopNavigation();
    }

    // 打完自動返回：只在主佇列正在跑它送出的返回時才中止，不動使用者自己按的傳送。
    private string AbortForReturn()
    {
        teleporter?.CancelReturnPrompt();
        if (!ChainManager.Queues.TryGetValue(MainQueue, out var main) || main.CurrentChain?.Name != nameof(ReturnChain))
        {
            return "沒有進行中的返回";
        }

        AbortQueues(id => id == MainQueue || id == nameof(ReturnChain) + WatcherSuffix);
        return StopNavigation();
    }

    private static void AbortQueues(Func<string, bool> match)
    {
        // 先收集再中止：取消收尾是別人的碼，可能回頭 ChainManager.Get() 新增佇列。
        var targets = ChainManager.Queues.Where(pair => match(pair.Key)).Select(pair => pair.Value).ToList();
        foreach (var queue in targets)
        {
            queue.Abort();
        }
    }

    // 中止了 BOCCHI 自己詠唱中的「返回」（中止前在詠唱、中止後旗標被清掉），而角色現在還在詠唱返回：取消詠唱。
    private static void CancelAbortedReturnCast(bool castBefore, string context)
    {
        if (!castBefore || ReturnChain.OwnCastInFlight)
        {
            return;
        }

        var player = Svc.Objects.LocalPlayer;
        if (player == null || ReturnCast.Classify(player.IsCasting, player.CastActionType, player.CastActionId) != ReturnCastKind.Return)
        {
            return;
        }

        Svc.Log.Information($"[BOCCHI][返回] {context}時 BOCCHI 自己施放的「返回」還在詠唱：{CancelCast()}");
    }

    // 遊戲自己的取消詠唱（同按取消鍵）；函式對不上時不呼叫。
    private static unsafe string CancelCast()
    {
        try
        {
            if (Hotbar.MemberFunctionPointers.CancelCast == null)
            {
                return "讀不到取消詠唱的遊戲函式，沒有取消，詠唱完仍會被傳回營地";
            }

            var ui = UIState.Instance();
            if (ui == null)
            {
                return "讀不到 UIState，沒有取消，詠唱完仍會被傳回營地";
            }

            ui->Hotbar.CancelCast();
            return "已送出取消詠唱（同按取消鍵），不會被傳回營地";
        }
        catch (Exception ex)
        {
            return $"取消詠唱失敗（{ex.GetType().Name}），詠唱完仍會被傳回營地";
        }
    }

    private static bool AnyChainActive()
    {
        return ChainManager.Queues.Values.Any(queue => queue.IsRunning || queue.QueueCount > 0);
    }

    // vnavmesh 先取消還在背景計算的尋路（算完才會開走），再清掉已經在走的路徑；Lifestream 只在忙碌時叫它中止。
    private string StopNavigation()
    {
        // 停下導航就作廢趕路登記：之後算完的迴避路徑不可以再讓角色動起來。作廢前先記下守衛要認的目的地。
        var vnavReady = vnav != null && vnav.IsReady();
        if (vnavReady)
        {
            guardDestinations.CaptureCurrent(TravelIntent.State);
        }

        OwnMoveDestinations.Recent.Clear();
        TravelIntent.Invalidate();
        var parts = new List<string>();

        if (!vnavReady)
        {
            parts.Add("vnavmesh 未載入");
        }
        else
        {
            parts.Add(TryInvoke("Nav.PathfindCancelAll", () => vnav!.CancelAllPathfinds()));
            parts.Add(TryInvoke("Path.Stop", () => vnav!.Stop()));
            navigationGuardUntil = Environment.TickCount64 + NavigationGuardMs;
            navigationGuardFired = false;
            guardForPotHunt = false;
        }

        if (lifestream != null && lifestream.IsReady())
        {
            try
            {
                if (lifestream.IsBusy())
                {
                    parts.Add(TryInvoke("Lifestream.Abort", () => lifestream.Abort()));
                }
            }
            catch (Exception ex)
            {
                parts.Add($"Lifestream.IsBusy 失敗（{ex.GetType().Name}）");
            }
        }

        return string.Join("、", parts);
    }

    private static string TryInvoke(string name, Action action)
    {
        try
        {
            action();
            return name;
        }
        catch (Exception ex)
        {
            return $"{name} 失敗（{ex.GetType().Name}：{ex.Message}）";
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var external = Interlocked.Exchange(ref pendingExternalStop, null);
        if (external != null)
        {
            StopAll(ExternalReason(external), true);
        }

        WatchRevive();

        WatchConfigWindowToggle();
        GuardNavigation();
        if (automator != null)
        {
            automator.automator.PruneSkips(automator);
        }

        var anyRunning = false;
        foreach (var kind in AutomationKindExtensions.All)
        {
            var running = IsRunning(kind);
            runningLastFrame[(int)kind] = running;
            anyRunning |= running;
        }

        var farming = runningLastFrame[(int)AutomationKind.MobFarmer];
        PhantomActions.Sync(runningLastFrame[(int)AutomationKind.IllegalMode] || farming, farming);

        if (!anyRunning)
        {
            WatchDownedWhileIdle();
            return;
        }

        if (!Svc.ClientState.IsLoggedIn)
        {
            StopAll(I18N.T("automation.reason.logged_out"), false, true, ActivityEnd.LoggedOut);
            return;
        }

        if (Svc.Objects.LocalPlayer?.IsDead == true)
        {
            LogDowned();
            CaptureDeathRestore();
            StopAll(I18N.T("automation.reason.dead"), true, true, ActivityEnd.Downed);
        }
    }

    // 停止之前記下在跑的項目；只記不恢復。
    private void CaptureDeathRestore()
    {
        Span<bool> running = stackalloc bool[AutomationKindExtensions.All.Length];
        if (DeathRestoreEnabled)
        {
            foreach (var kind in AutomationKindExtensions.All)
            {
                running[(int)kind] = IsRunning(kind);
            }
        }

        // 選項關著時記一份空的：同時丟掉選項開著時留下的舊清單。
        ClearDeathRestoreText();
        if (!deathRestore.Capture(running))
        {
            return;
        }

        deathRestoreNames = string.Join("、", AutomationKindExtensions.All.Where(deathRestore.Contains).Select(GetName));
        deathRestoreLabel = I18N.T("automation.panel.death_restore", new Dictionary<string, string> { ["names"] = deathRestoreNames });
        Svc.Log.Information($"[BOCCHI][倒地恢復] 記下倒地時停下的自動化「{deathRestoreNames}」：復活時提示，按恢復才恢復");
    }

    // 復活時提示一次（聊天＋通知）；不會自己恢復。
    private void PromptDeathRestore()
    {
        if (!DeathRestoreEnabled || !deathRestore.TakePrompt())
        {
            return;
        }

        Svc.Log.Information($"[BOCCHI][倒地恢復] 已復活，提示可以恢復「{deathRestoreNames}」（主視窗的恢復鈕或 /bocchi restore；不會自己恢復）");
        var args = new Dictionary<string, string> { ["names"] = deathRestoreNames };
        Svc.Chat.Print(I18N.T("automation.messages.death_restore_prompt", args));
        try
        {
            Svc.NotificationManager.AddNotification(new Notification
            {
                Title = I18N.T("automation.messages.death_restore_notification_title"),
                Content = I18N.T("automation.messages.death_restore_notification", args),
                Type = NotificationType.Info,
                Minimized = false,
                InitialDuration = TimeSpan.FromSeconds(15),
            });
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[BOCCHI][倒地恢復] 通知沒有送出（{ex.GetType().Name}）：{ex.Message}；聊天視窗的提示照常");
        }
    }

    private void InvalidateDeathRestore(string why)
    {
        if (deathRestore.Invalidate())
        {
            Svc.Log.Information($"[BOCCHI][倒地恢復] 不再保留倒地時停下的「{deathRestoreNames}」（{why}）");
        }

        ClearDeathRestoreText();
    }

    private void ClearDeathRestoreText()
    {
        deathRestoreNames = string.Empty;
        deathRestoreLabel = string.Empty;
    }

    // 恢復時清單已先取走，所以恢復自己開始的項目不會走到作廢。
    private void InvalidateDeathRestoreOnManualStart(AutomationKind kind)
    {
        if (deathRestore.HasSnapshot)
        {
            InvalidateDeathRestore(I18N.T("automation.reason.manual_start", new Dictionary<string, string> { ["name"] = GetName(kind) }));
        }
    }

    // 沒有自動化在跑時，在新月島倒地也寫 [倒地]／[復活]（不停任何東西、不記恢復清單）。
    private void WatchDownedWhileIdle()
    {
        if (downedAt != 0 || !Svc.ClientState.IsLoggedIn || Svc.Objects.LocalPlayer?.IsDead != true || !ZoneData.IsInOccultCrescent())
        {
            return;
        }

        LogDowned();
    }

    // 倒地與復活各寫一行；復活後不會自己恢復自動化（無人值守的恢復是紅線），只記錄。
    private void LogDowned()
    {
        try
        {
            LogDownedCore();
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[BOCCHI][倒地] 寫記錄時出錯（不影響停止）：{ex.GetType().Name}：{ex.Message}");
        }
    }

    private void LogDownedCore()
    {
        if (downedAt != 0)
        {
            return;
        }

        var player = Svc.Objects.LocalPlayer;
        downedPosition = player?.Position ?? default;
        downedAt = Environment.TickCount64;
        var activity = automator?.automator.Activity;
        var running = string.Join("、", AutomationKindExtensions.All.Where(IsRunning).Select(GetName));
        downedWhileRunning = running.Length > 0;
        var label = activity == null ? "-" : $"{activity.KindLabel}#{activity.data.Id}";
        Svc.Log.Information(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[BOCCHI][倒地] 活動={label} 活動名=「{activity?.GetName() ?? "-"}」 活動狀態={activity?.state.ToString() ?? "-"} 狀態={stateManager?.GetState().ToString() ?? "?"} "
            + $"位置=({downedPosition.X:F1},{downedPosition.Y:F1},{downedPosition.Z:F1}) 自動化=「{running}」 自動化狀態={(downedWhileRunning ? "開" : "關")}"));
    }

    private void WatchRevive()
    {
        if (downedAt == 0)
        {
            return;
        }

        try
        {
            WatchReviveCore();
        }
        catch (Exception ex)
        {
            downedAt = 0;
            Svc.Log.Warning($"[BOCCHI][復活] 寫記錄時出錯：{ex.GetType().Name}：{ex.Message}");
        }
    }

    private void WatchReviveCore()
    {

        if (!Svc.ClientState.IsLoggedIn)
        {
            downedAt = 0;
            InvalidateDeathRestore(I18N.T("automation.reason.logged_out"));
            return;
        }

        var player = Svc.Objects.LocalPlayer;
        if (player == null || player.IsDead)
        {
            return;
        }

        var moved = System.Numerics.Vector3.Distance(player.Position, downedPosition);
        Svc.Log.Information(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[BOCCHI][復活] 距倒下={(Environment.TickCount64 - downedAt) / 1000.0:F1} 位移={moved:F1} 自動化={(downedWhileRunning ? "未恢復" : "倒地時沒在跑")}"));
        downedAt = 0;
        PromptDeathRestore();
    }

    private void GuardNavigation()
    {
        if (navigationGuardUntil == 0)
        {
            return;
        }

        // 讓 EurekaHelper 尋寶時非法模式還開著：讓位結束就收掉，免得停掉 BOCCHI 接著送的新路線。
        var released = guardForPotHunt ? automator?.automator.IsHoldingForPotHunt != true : IsAnyMovementRunning();
        if (Environment.TickCount64 > navigationGuardUntil || released || vnav == null)
        {
            navigationGuardUntil = 0;
            return;
        }

        try
        {
            var verdict = JudgeCurrentPath(guardDestinations, out var end);
            switch (verdict)
            {
                case OwnPathVerdict.Idle:
                    return;

                case OwnPathVerdict.Foreign:
                    // 別人送的移動會蓋掉 vnavmesh 裡 BOCCHI 還沒算完的請求，之後不會再有 BOCCHI 的路線開走。
                    navigationGuardUntil = 0;
                    Svc.Log.Information($"[BOCCHI][自動化] 停止後 vnavmesh 開始的移動不是 BOCCHI 送的（終點 {FormatPoint(end)}，"
                                        + $"離 BOCCHI 送過的目的地 {FormatDistance(guardDestinations.NearestXZ(end!.Value))} 米）：不停，交給送出它的插件或你自己。");
                    return;
            }

            vnav.Stop();
            if (!navigationGuardFired)
            {
                navigationGuardFired = true;
                var why = verdict == OwnPathVerdict.Own ? $"終點 {FormatPoint(end)} 是 BOCCHI 送過的目的地" : "讀不到路線終點，照舊停";
                Svc.Log.Information($"[BOCCHI][自動化] 停止後 vnavmesh 又開始移動（多半是停止前送出的尋路剛算完；{why}），已再停一次。");
            }
        }
        catch (Exception)
        {
            navigationGuardUntil = 0;
        }
    }

    private static string FormatDistance(float meters)
    {
        return float.IsFinite(meters) ? meters.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "?";
    }

    /// <summary>有沒有會自己移動的自動化在跑（非法模式、自動打怪、兩種巡迴）。</summary>
    public bool IsAnyMovementRunning()
    {
        foreach (var kind in AutomationKindExtensions.All)
        {
            if (kind.DrivesMovement() && IsRunning(kind))
            {
                return true;
            }
        }

        return false;
    }

    // 設定視窗的「啟用」勾選框直接改設定值、不經過這裡：取消勾選要補做收尾，勾選要照樣檢查能不能開始。
    private void WatchConfigWindowToggle()
    {
        var enabled = automator?.Config.Enabled == true;
        if (enabled == illegalSeenEnabled)
        {
            return;
        }

        illegalSeenEnabled = enabled;
        var source = I18N.T("automation.source.config_window");

        if (!enabled)
        {
            StopOne(AutomationKind.IllegalMode, source, false, true, false);
            return;
        }

        if (!CanStartIgnoringRunning(AutomationKind.IllegalMode, out var whyNot))
        {
            automator!.Config.Enabled = false;
            illegalSeenEnabled = false;
            Svc.Log.Information($"[BOCCHI][自動化] 沒有開始「{GetName(AutomationKind.IllegalMode)}」（{source}）：{whyNot}");
            Svc.Chat.Print(I18N.T("automation.messages.cannot_start", new Dictionary<string, string> { ["name"] = GetName(AutomationKind.IllegalMode), ["reason"] = whyNot }));
            return;
        }

        Svc.Log.Information($"[BOCCHI][自動化] 開始「{GetName(AutomationKind.IllegalMode)}」（{source}）");
        InvalidateDeathRestoreOnManualStart(AutomationKind.IllegalMode);
        ApplyQuickRestartSkip(source);
    }

    private void OnTerritoryChanged(ushort territory)
    {
        var reason = I18N.T(territory == ZoneData.SOUTHHORN ? "automation.reason.zone_changed" : "automation.reason.left_zone");

        // 進島也停：新月島裡才能開始，所以從別處帶進來的只可能是殘留，不能讓它一進島就動起來。
        downedAt = 0;
        InvalidateDeathRestore(reason);
        if (IsAnyRunningOrWasRunning())
        {
            StopAll(reason, true, false, ActivityEnd.ZoneChanged);
        }

        InvalidateHuntProgress(HuntProgressInvalidation.ZoneChanged, reason);
        automator?.automator.ClearSkips(reason);
    }

    private void OnLogout(int type, int code)
    {
        var reason = I18N.T("automation.reason.logged_out");
        InvalidateDeathRestore(reason);
        if (IsAnyRunningOrWasRunning())
        {
            StopAll(reason, false, false, ActivityEnd.LoggedOut);
        }

        InvalidateHuntProgress(HuntProgressInvalidation.LoggedOut, reason);
        automator?.automator.ClearSkips(reason);
    }

    // 停止一律先於作廢：正在跑的先被 StopAll 轉成暫停，這裡再讓它失效。
    private void InvalidateHuntProgress(HuntProgressInvalidation why, string reason)
    {
        foreach (var kind in AutomationKindExtensions.All)
        {
            if (GetHunter(kind) is { } hunter && hunter.Progress.Invalidate(why))
            {
                Svc.Log.Information($"[BOCCHI][巡迴進度] 進度失效「{GetName(kind)}」（{reason}）：已跑 {hunter.ProgressText} 點；續跑停用，要再跑請按開始從頭跑");
            }
        }
    }

    private static void LogAbandon(AutomationKind kind, Hunter hunter, string reason)
    {
        Svc.Log.Information($"[BOCCHI][巡迴進度] 放棄進度「{GetName(kind)}」（{reason}）：已跑 {hunter.ProgressText} 點");
    }

    private void LogKept(AutomationKind kind, string reason, bool announce)
    {
        if (GetHunter(kind) is not { Progress.State: HuntProgressState.Paused } hunter)
        {
            return;
        }

        Svc.Log.Information($"[BOCCHI][巡迴進度] 保留進度「{GetName(kind)}」（{reason}）：已跑 {hunter.ProgressText} 點；不會自己續跑，要接著跑請按續跑或 /bocchi resume {kind.CommandKey()}");
        if (announce)
        {
            Svc.Chat.Print(I18N.T("automation.messages.progress_kept", new Dictionary<string, string> { ["name"] = GetName(kind), ["progress"] = hunter.ProgressText, ["key"] = kind.CommandKey() }));
        }
    }

    private static bool NotPausable(AutomationKind kind, out string whyNot)
    {
        whyNot = I18N.T("automation.messages.not_pausable", new Dictionary<string, string> { ["name"] = GetName(kind) });
        return false;
    }

    private static string ExternalReason(string label)
    {
        return I18N.T("automation.reason.external", new Dictionary<string, string> { ["source"] = label });
    }

    private static bool Unavailable(out string whyNot)
    {
        whyNot = I18N.T("automation.blocked.module_unavailable");
        return false;
    }

    private bool RequirePlugins(out string whyNot, bool needVnav, bool needLifestream)
    {
        var now = Environment.TickCount64;
        if (!pluginsChecked || now - pluginCheckedAt >= 1000)
        {
            pluginsChecked = true;
            pluginCheckedAt = now;
            vnavLoaded = vnav?.IsReady() == true;
            lifestreamLoaded = lifestream?.IsReady() == true;
        }

        if ((!needVnav || vnavLoaded) && (!needLifestream || lifestreamLoaded))
        {
            whyNot = string.Empty;
            return true;
        }

        var missing = new List<string>();
        if (needVnav && !vnavLoaded)
        {
            missing.Add("vnavmesh");
        }

        if (needLifestream && !lifestreamLoaded)
        {
            missing.Add("Lifestream");
        }

        whyNot = I18N.T("automation.blocked.missing_plugins", new Dictionary<string, string> { ["plugins"] = string.Join("、", missing) });
        return false;
    }

    /// <summary>停止時要記的原因：活動帳的結束種類、巡迴結束行的種類與原因、進度會不會接著失效。</summary>
    private readonly record struct StopContext(ActivityEnd Activity, HuntEnd Hunt, string Reason, bool Invalidating);

    private static T? Find<T>(Plugin plugin) where T : class, IModule
    {
        try
        {
            return plugin.Modules.GetModule<T>();
        }
        catch (UnableToLoadModuleException)
        {
            Svc.Log.Information($"[BOCCHI][自動化] 模組 {typeof(T).Name} 沒有載入，對應的自動化無法開始。");
            return null;
        }
    }

    private static T? FindIpc<T>(Plugin plugin) where T : IPCSubscriber
    {
        try
        {
            return plugin.IPC.GetSubscriber<T>();
        }
        catch (UnableToLoadIpcSubscriberException)
        {
            return null;
        }
    }
}
