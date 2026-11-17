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
// 自動停止：任何區域切換、角色無法戰鬥、登出／斷線、插件卸載、開著「兩歧塔開放時到傳送魔紋等候」時非法模式中進塔；停止時一併中止動作鏈、導航與自己開的戰鬥 AI。
// 輔助職業技能清單跟著非法模式／自動打怪的執行狀態每幀同步：不論從哪條路徑停下，下一幀就交還 WrathCombo 的租約。
// 寶箱／幸運胡蘿蔔巡迴的進度：暫停、全部停止（含 IPC 急停）、自動停止都保留成「暫停」，但一律不會自己續跑；單項停止、從頭開始、放棄才丟掉；換區與登出讓保留的進度失效。暫停中不算在跑，不佔移動自動化的互斥。
// 停止、全部停止、暫停、非法模式讓 EurekaHelper 尋寶中止了 BOCCHI 自己詠唱中的「返回」時，詠唱一起取消（只取消返回，不碰別的詠唱）。
// vnavmesh 路線：使用者要的停止（急停、全部停止、單項停止、暫停、換一場；換區／登出／倒地走全部停止）刻意全域停（StopNavigationByUser）；
// 自動化自己的停止（非法模式換目標、巡迴自己停下、插件卸載）只停終點是 BOCCHI 送過的目的地的路線（StopOwnNavigation）。
public sealed partial class AutomationControl : IDisposable
{
    private const string MainQueue = Plugin.MainChainQueue;

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

    // 上一幀是否在兩歧塔裡；非法模式關著也追蹤，所以在塔裡手動開始的不會被停。
    private bool insideForkedTower;

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

    /// <summary>使用者停掉一項（主視窗、指令、標題列、設定頁開關、停止組合）：連巡迴保留的進度一起丟掉，導航照舊全域停。沒在跑時只做收尾不報告。回傳它原本是否在跑。</summary>
    public bool Stop(AutomationKind kind, string reason, bool announce)
    {
        return StopOneByUser(kind, reason, announce, IsRunning(kind) || runningLastFrame[(int)kind]);
    }

    /// <summary>巡迴自己停下（卡住、讀不到成本表、送不出移動）：進度保留成暫停，vnavmesh 只停 BOCCHI 自己的路線。只給寶箱／幸運胡蘿蔔巡迴用。回傳它原本是否在跑。</summary>
    public bool StopKeepingProgress(AutomationKind kind, string reason, bool announce)
    {
        return StopOneKeepingProgress(kind, reason, announce, IsRunning(kind) || runningLastFrame[(int)kind]);
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
            var navigation = AbortForMovementKindByUser(kind);
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

            var navigation = wasRunning.Count > 0 || hadChains ? StopNavigationByUser() : "沒有要停的";
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
    /// 非法模式自己換目標時用：中止正在跑的動作鏈（含 ##watcher 佇列），vnavmesh 只停 BOCCHI 自己的路線（別的插件或你自己送的不停），不中止 Lifestream；
    /// 不改任何自動化的執行狀態。只在 framework 執行緒呼叫；回傳給 log 用的導航處理結果。
    /// </summary>
    public string AbortIllegalModeTravel()
    {
        return AbortForMovementKindOwn(AutomationKind.IllegalMode, false);
    }

    /// <summary>換一場（使用者按的）：收的範圍與停止非法模式相同（含 BOCCHI 自己詠唱中的「返回」），但不改任何自動化的執行狀態。只在 framework 執行緒呼叫。</summary>
    public string AbortIllegalModeTravelForSkip()
    {
        var returnCast = ReturnChain.OwnCastInFlight;
        var navigation = AbortForMovementKindByUser(AutomationKind.IllegalMode);
        CancelAbortedReturnCast(returnCast, "換一場");
        return navigation;
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

        // 卸載時動作鏈由 Ocelot 自己收；這裡只負責不讓 vnavmesh 帶著角色繼續走到 BOCCHI 上一個目的地。
        // 卸載不是使用者對導航的操作：別的插件（例：讓位中的 EurekaHelper）正在走的路線不停。
        var context = new StopContext(ActivityEnd.Unloaded, HuntEnd.Unloaded, "插件卸載", false);
        foreach (var kind in AutomationKindExtensions.All)
        {
            ResetState(kind, true, context, !Svc.Framework.IsFrameworkUnloading);
        }

        Svc.Log.Information($"[BOCCHI][自動化] 插件卸載時仍有自動化在跑，已停止；導航：{StopOwnNavigation(true)}");
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
