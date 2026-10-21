using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using BOCCHI.Automation;
using BOCCHI.Chains;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules;
using BOCCHI.Modules.Pathfinder;
using BOCCHI.Modules.StateManager;
using BOCCHI.Windows;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Bindings.ImGui;
using Ocelot;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;
using Ocelot.IPC;
using Ocelot.Modules;
using TextCopy;

namespace BOCCHI.Pathfinding;

public abstract class Hunter
{
    protected const float DISTANCE_TO_NODE_TO_USE = 2f;

    // 同一步超過這麼久沒完成就當成卡住並停止（例如 vnavmesh 一直找不到路）。
    private const long StepStallLimitMs = 180_000;

    private int watchedStep = -1;

    private long watchedStepSince;

    // 步驟處理器直接從 Update 呼叫，只在 StepProcessor 沒有在跑或排隊的動作鏈時跑；
    // 間隔不能更密：vnavmesh 還在算路、Path.IsRunning 為 false 時會重下尋路。
    private const long StepTickIntervalMs = 150;

    private long nextStepTickAt;

    private readonly MoveRequestGuard moveGuard = new();

    private readonly HuntYieldGate yieldGate = new();

    // 測試台不經建構子建出時是 null（當成 Lifestream 不忙）。
    private readonly LifestreamBusyProbe? lifestreamBusy;

    // 讓位狀態屬於哪一步；換步時只有讓位已滿上限才重來（讓位中換步照樣讓位）。
    private int yieldStep = -1;

    // 這一拍 RequestMove 因手動移動沒送：也不叫坐騎。
    private bool holdingForManual;

    // 換目的地時舊的 SimpleMove 沒取消掉（vnavmesh 還有別筆尋路）：它算完開走就停下、改送新的目的地。
    private bool staleMovePending;

    // 步驟處理器擲例外時每 150 毫秒重試：同一步、同一種例外只寫一次帶堆疊的 Error，之後寫 Debug（換一趟巡迴重算）。
    private int stepErrorIndex = -1;

    private Type? stepErrorType;

    private readonly HuntCombatWait combatWait = new();

    // 最後一次看到戰鬥旗標的時刻（0＝沒看過）：「回營地」那一步要脫戰滿 HuntCombatWait.SettleMs 才停下改施放返回。
    private long lastCombatSeenAt;

    private readonly PlanStartGate planGate = new();

    private readonly HuntProgress progress = new();

    private readonly LedgeClimb ledgeClimb = new();

    private const uint JumpActionId = 2;

    // 下一次規劃要扣掉已經跑過的點（續跑）；規劃一開始就清掉。
    private bool resumePlan;

    // 規劃出來沒有任何點：下一拍直接結束，不再重新規劃。
    private bool routeEmpty;

    // 處理器已經跑過至少一次的步驟索引（那一步可能已經在走）；-1＝目前這一步還沒開始。
    private int startedStep = -1;

    // 只有 TracksCoverage 的巡迴才用到，用到時才建。
    private Func<uint, bool>? coveredPredicate;

    private List<uint>? planPriority;

    // 這一輪倒過來走：從頭開始時照 ConfiguredDirection 決定，續跑沿用。
    private bool roundReverse;

    // 上一輪的方向（每輪交替用）；null＝插件載入後還沒跑過。
    private bool? lastRoundReverse;

    // 這一段（開始或續跑到停下）的統計，給 [寶箱巡迴] 結束行；segmentStartedAt 為 0＝沒有進行中的一段。
    private long segmentStartedAt;

    private string segmentStart = "從頭";

    private int segmentVisitedAtStart;

    // 這一段開始時的累計開箱數（見 ChestsOpenedSoFar）。
    private int bronzeAtStart;

    private int silverAtStart;

    private int bunnyOpened;

    private long segmentYieldMs;

    // 這一段第一次讓位／接續寫 Information，之後寫 Debug。
    private bool yieldReported;

    private bool resumeReported;

    private bool externalReported;

    private bool touchReported;

    private bool cappedTouchReported;

    private HandsOnProbe? handsOn;

    protected static readonly Vector4 UnknownColor = new(0.62f, 0.62f, 0.62f, 1f);

    // 最近一次規劃的範圍；Total 為 0＝這一輪還沒規劃（主視窗畫 ?）。只在 framework 執行緒讀寫。
    private HuntRange range;

    private int rangeVersion;

    // 主視窗那一列與說明框的文字只在範圍或語言變了才重組。
    private int rangeTextVersion;

    private string? rangeTemplate;

    private string rangeText = string.Empty;

    private string rangeTooltip = string.Empty;

    private readonly Plugin plugin;

    protected StateManagerModule states;

    protected VNavmesh vnav;

    protected PathfinderConfig config;

    protected bool running;

    protected IPathfinder? pathfinder;

    protected List<PathfinderStep> Steps = [];

    protected int stepIndex = 0;

    protected float distance = 0f;

    protected Stopwatch stopwatch = new();

    protected PathfinderStep CurrentStep
    {
        get => Steps[stepIndex];
    }

    protected string JSON = "";

    protected ChainQueue StepProcessor
    {
        get => ChainManager.Get(GetType().FullName ?? "Hunter");
    }

    protected Dictionary<PathfinderStepType, Func<bool>> Handlers;

    protected Hunter(Module module)
    {
        plugin = module.Plugin;
        states = module.GetModule<StateManagerModule>();
        vnav = module.GetIPCSubscriber<VNavmesh>();
        lifestreamBusy = new LifestreamBusyProbe(module.GetIPCSubscriber<Lifestream>());
        config = module.PluginConfig.PathfinderConfig;

        Handlers = new Dictionary<PathfinderStepType, Func<bool>>
        {
            { PathfinderStepType.WalkToNode, WalkToNodeHandler },
            { PathfinderStepType.ReturnToBaseCamp, ReturnToBaseCampHandler },
            { PathfinderStepType.WalkToAethernet, WalkToAethernetHandler },
            { PathfinderStepType.TeleportToAethernet, TeleportToAethernetHandler },
            { PathfinderStepType.DigCarrot, DigCarrotHandler },
        };
    }

    protected abstract IEnumerable<IGameObject> GetValidObjects();

    protected abstract Vector3 GetDestinationForCurrentStep();

    protected float GetDetectionRange()
    {
        return config.DetectionRange;
    }

    /// <summary>
    /// 建立這一輪的路徑搜尋器。
    /// 🔴 可以回 null:原生資料(例如 LayoutWorld 的 ActiveLayout)還沒就緒時,實作要
    /// fail-closed 放棄而不是硬解參考 null。Update() 在 pathfinder 仍為 null 時會下一拍重試。
    /// </summary>
    protected abstract IPathfinder? CreatePathfinder();

    protected abstract Func<Chain> GetInteractionChain(IGameObject obj);

    protected abstract List<uint> GetValidNodes(int max);

    public abstract AutomationKind Kind { get; }

    public bool IsRunning
    {
        get => running;
    }

    /// <summary>步驟用的動作鏈佇列名稱；停止時要一起中止。</summary>
    public string StepQueueName
    {
        get => GetType().FullName ?? "Hunter";
    }

    /// <summary>跑過哪些點、能不能續跑；只在 framework 執行緒讀寫。</summary>
    public HuntProgress Progress
    {
        get => progress;
    }

    /// <summary>給記錄與說明框用的「已跑／規劃」點數，例如 3/12；還沒規劃完是 3/?。會配置，不要每幀呼叫。</summary>
    public string ProgressText
    {
        get => $"{progress.VisitedCount}/{(progress.PlannedCount > 0 ? progress.PlannedCount.ToString() : "?")}";
    }

    /// <summary>暫停後可以續跑多久（毫秒）；0＝不限。</summary>
    public virtual long ResumeExpiryMs
    {
        get => config.ResumeExpiryMinutes > 0 ? config.ResumeExpiryMinutes * 60_000L : 0;
    }

    /// <summary>true＝規劃與執行時略過 <see cref="IsNodeCovered"/> 的點，並接受 <see cref="TryPickDetour"/> 插隊。</summary>
    protected virtual bool TracksCoverage
    {
        get => false;
    }

    /// <summary>這個點已經確認過、不必再走。</summary>
    protected virtual bool IsNodeCovered(uint node)
    {
        return false;
    }

    /// <summary>規劃前調整：從 <paramref name="planned"/> 增刪要跑的點，要先去的點放進 <paramref name="priority"/>。</summary>
    /// <param name="inRange">區域等級範圍內的全部點（不含續跑扣掉的）；不可改。</param>
    protected virtual void AdjustPlan(List<uint> inRange, List<uint> planned, List<uint> priority)
    {
    }

    /// <summary>每一拍執行步驟前問一次：要不要先去另一個點。append＝排到最後而不是插在目前這一步前面。</summary>
    protected virtual bool TryPickDetour(out uint node, out bool append)
    {
        node = 0;
        append = false;
        return false;
    }

    /// <summary>走路步驟（往點或乙太之晶）每一拍在插隊之後問一次：要不要先去挖一根幸運胡蘿蔔。讓位給手動移動時不問。</summary>
    /// <remarks>回 true 時在目前這一步前面插一個 DigCarrot；那一步做完就移除，接回原本那一步。</remarks>
    protected virtual bool TryPickCarrot(out ulong objectId, out Vector3 position)
    {
        objectId = 0;
        position = default;
        return false;
    }

    /// <summary>DigCarrot 那一步每一拍問一次要怎麼做；只在步驟處理器沒在跑時呼叫。</summary>
    protected virtual CarrotStepAction OnCarrotStep(PathfinderStep step)
    {
        return CarrotStepAction.Done;
    }

    /// <summary>DigCarrot 那一步走不到（太久沒起步或同一步卡住）：那一步已經移除，子類別寫記錄。</summary>
    protected virtual void OnCarrotUnreachable(PathfinderStep step, string what)
    {
    }

    /// <summary>正在跑的巡迴被停止、暫停或重設時（<see cref="ResetRun(bool, HuntEnd, string?, bool)"/>）呼叫一次。</summary>
    protected virtual void OnRunStopped()
    {
    }

    /// <summary>走到點的偵測範圍內、判斷那裡沒有目標物件。</summary>
    protected virtual void OnNodeAbsent(uint node)
    {
    }

    /// <summary>走向某個點的那一步完成（不論有沒有送出互動）。</summary>
    protected virtual void OnNodeStepDone(uint node)
    {
    }

    /// <summary>開始或續跑（resumed）時呼叫一次。</summary>
    protected virtual void OnRunStarted(bool resumed)
    {
    }

    /// <summary>詠唱與讀圖等完、送出規劃之前每拍呼叫：true＝這一拍先不規劃（子類別在主動作鏈上有前置動作）。</summary>
    protected virtual bool HoldBeforePlan()
    {
        return false;
    }

    /// <summary>走向某個點失敗（太久沒起步、或同一步卡住）時問一次：true＝跳過這一步、巡迴繼續；false＝照舊停止巡迴。</summary>
    protected virtual bool TrySkipUnreachableNode(uint node)
    {
        return false;
    }

    /// <summary>這個點在網格接不上的低台上：走向它之前先跳上去（見 <see cref="LedgeClimb"/>）。</summary>
    protected virtual bool TryGetLedge(uint node, out HuntLedge ledge)
    {
        ledge = default;
        return false;
    }

    /// <summary>規劃第一段時向 vnavmesh 量步行路長用的點座標；false＝不知道（這個點不量）。</summary>
    protected virtual bool TryGetNodePosition(uint node, out Vector3 position)
    {
        position = default;
        return false;
    }

    /// <summary>這種巡迴有方向：主視窗「本輪」與結束行帶方向。false＝一律正向、不帶，也不讀 <see cref="ConfiguredDirection"/>。</summary>
    protected virtual bool HasDirection
    {
        get => false;
    }

    /// <summary>設定的走法；只在從頭開始時讀。</summary>
    protected virtual HuntDirection ConfiguredDirection
    {
        get => HuntDirection.Forward;
    }

    /// <summary>這一輪（開始時決定、續跑沿用）是不是倒過來走。</summary>
    public bool RoundReversed
    {
        get => roundReverse;
    }

    /// <summary>設定的走法（給畫面與記錄看）。</summary>
    public HuntDirection Direction
    {
        get => ConfiguredDirection;
    }

    /// <summary>這一輪規劃好的步驟（還沒規劃完是空的）；只在框架執行緒當幀讀，不要保留。</summary>
    public IReadOnlyList<PathfinderStep> PlannedSteps
    {
        get => Steps;
    }

    /// <summary>目前這一步在 <see cref="PlannedSteps"/> 的索引；等於步數＝走完了。</summary>
    public int CurrentStepIndex
    {
        get => stepIndex;
    }

    /// <summary>這一輪只巡部分路線：<see cref="IsInRound"/> 有回 false 的點。false＝全部都巡。</summary>
    protected virtual bool HasRoundFilter
    {
        get => false;
    }

    /// <summary>這一輪要不要巡這個點（開始時決定、續跑沿用）；false 的點不排、不算進「本輪」。只在 <see cref="HasRoundFilter"/> 時問。</summary>
    protected virtual bool IsInRound(uint node)
    {
        return true;
    }

    /// <summary>[寶箱巡迴] 結束行的「路線=」欄位；沒有路線的巡迴是 -。</summary>
    protected virtual string RoundRouteLabel
    {
        get => "-";
    }

    /// <summary>主視窗「本輪」那一列方向之後要接的文字；空字串＝不接。只在範圍變了才問。</summary>
    protected virtual string RoundRangeSuffix(Module<Plugin, Config> module)
    {
        return string.Empty;
    }

    /// <summary>主視窗巡迴區段在進度之後要不要多畫幾列（沒跑過也畫）。</summary>
    protected virtual bool HasExtraRows
    {
        get => false;
    }

    protected virtual void DrawExtraRows(Module<Plugin, Config> module)
    {
    }

    public bool CanResume(out HuntProgressInvalidation why)
    {
        return progress.CanResume(Environment.TickCount64, ResumeExpiryMs, out why);
    }

    /// <summary>由 AutomationControl 呼叫：從頭開始，丟掉之前保留的進度。</summary>
    public void Start()
    {
        running = true;
        watchedStep = -1;
        startedStep = -1;
        moveGuard.Reset();
        ResetYield();
        combatWait.Reset();
        planGate.Reset();
        resumePlan = false;
        routeEmpty = false;
        progress.BeginFresh();
        ledgeClimb.Reset();
        ResetShardStairs();
        roundReverse = HasDirection && ConfiguredDirection switch
        {
            HuntDirection.Reverse => true,
            HuntDirection.Alternate => lastRoundReverse == false,
            _ => false,
        };
        lastRoundReverse = roundReverse;
        SetRange(default);
        stopwatch.Restart();
        BeginSegment("從頭");
        OnRunStarted(false);
    }

    /// <summary>由 AutomationControl 呼叫：從角色目前位置重新規劃還沒跑過的點。回 false＝沒有可以續跑的進度，什麼都沒動。</summary>
    public bool Resume()
    {
        if (!progress.Resume(Environment.TickCount64, ResumeExpiryMs))
        {
            return false;
        }

        running = true;
        watchedStep = -1;
        startedStep = -1;
        moveGuard.Reset();
        ResetYield();
        combatWait.Reset();
        planGate.Reset();
        resumePlan = true;
        routeEmpty = false;
        ledgeClimb.Reset();
        ResetShardStairs();
        SetRange(default);
        stopwatch.Start();
        BeginSegment("續跑");
        OnRunStarted(true);
        return true;
    }

    /// <summary>
    /// 只重設巡迴狀態（冪等）；動作鏈與導航由 AutomationControl 收。
    /// keepProgress＝正在跑的進度轉成暫停保留（已經暫停或失效的照舊）；false＝連保留的進度一起丟掉。
    /// </summary>
    public void ResetRun(bool keepProgress)
    {
        ResetRun(keepProgress, keepProgress ? HuntEnd.AutoStopped : HuntEnd.Stopped, null, false);
    }

    /// <param name="end">正在跑時，[寶箱巡迴] 結束行記的結束種類。</param>
    /// <param name="reason">給記錄看的原因；null＝沒有。</param>
    /// <param name="invalidating">呼叫端接著就讓保留的進度失效（換區、登出）。</param>
    public void ResetRun(bool keepProgress, HuntEnd end, string? reason, bool invalidating)
    {
        // 先寫記錄再動進度：丟棄進度會把已跑的點數清成 0。
        if (running)
        {
            EndSegment(end, reason, HuntSegment.ProgressLabel(end, keepProgress, invalidating));
            OnRunStopped();
        }

        if (keepProgress)
        {
            progress.Pause(Environment.TickCount64);
        }
        else
        {
            progress.Clear();
        }

        stopwatch.Stop();
        running = false;
        stepIndex = 0;
        Steps.Clear();
        pathfinder = null;
        stepErrorIndex = -1;
        ledgeClimb.Reset();
        ResetShardStairs();
        watchedStep = -1;
        startedStep = -1;
        moveGuard.Reset();
        ResetYield();
        combatWait.Reset();
        planGate.Reset();
        resumePlan = false;
        routeEmpty = false;
    }

    private void ResetYield()
    {
        yieldGate.Reset();
        yieldStep = -1;
        holdingForManual = false;
    }

    public void Update()
    {
        if (progress.State == HuntProgressState.Paused && progress.Expire(Environment.TickCount64, ResumeExpiryMs))
        {
            Svc.Log.Information($"[BOCCHI][巡迴進度] 進度失效「{AutomationControl.GetName(Kind)}」（暫停超過 {config.ResumeExpiryMinutes} 分鐘）：已跑 {ProgressText} 點；"
                                + "續跑停用，要再跑請按開始從頭跑");
        }

        if (!running || Plugin.Chain.IsRunning)
        {
            return;
        }

        if (IsStalled())
        {
            if (SkipUnreachableStep($"超過 {StepStallLimitMs / 1000} 秒沒完成"))
            {
                return;
            }

            var reason = I18N.T("automation.reason.stalled", new Dictionary<string, string>
            {
                ["step"] = (stepIndex + 1).ToString(),
                ["seconds"] = (StepStallLimitMs / 1000).ToString(),
            });
            plugin.Automation.Stop(Kind, reason, true, true);
            return;
        }

        if (routeEmpty)
        {
            FinishEmptyRoute();
            return;
        }

        if (pathfinder == null && Steps.Count <= 0)
        {
            pathfinder = CreatePathfinder();
        }

        MaintainWatcherChain();
    }

    private void MaintainWatcherChain()
    {
        if (Plugin.Chain.IsRunning)
        {
            return;
        }

        if (pathfinder is { State: PathfinderState.LoadFailed })
        {
            // 不重建規劃器重讀：同一個檔每一拍都會再失敗一次。
            pathfinder = null;
            Svc.Log.Information($"[BOCCHI][尋路] 「{AutomationControl.GetName(Kind)}」讀不到尋路成本表（原因見上一行 Error），停止巡迴");
            plugin.Automation.Stop(Kind, I18N.T("automation.reason.data_load_failed"), true, true);
            return;
        }

        if (pathfinder != null && pathfinder.State != PathfinderState.PathfindingDone)
        {
            if (HoldPlanStart() || HoldBeforePlan())
            {
                return;
            }

            Plugin.Chain.Submit(() =>
            {
                Task<List<PathfinderStep>> steps = null!;
                StartLegSurvey? survey = null;
                var valid = PlanNodes();
                var priority = planPriority;
                if (valid.Count == 0)
                {
                    return Chain.Create("Hunter.Pathfinding").Then(_ =>
                    {
                        pathfinder = null;
                        routeEmpty = true;
                    });
                }

                // Prep pathfinding
                return Chain.Create("Hunter.Pathfinding")
                    .Then(new TaskManagerTask(() => pathfinder?.State is PathfinderState.FileLoaded or PathfinderState.LoadFailed))
                    .BreakIf(() => pathfinder?.State != PathfinderState.FileLoaded)
                    .Then(_ => survey = BeginStartLegSurvey(valid, priority))
                    .Then(_ => survey == null || survey.IsDone(Environment.TickCount64))
                    .Then(_ => steps = PlanPath(valid, priority, survey))
                    .Then(new TaskManagerTask(() => steps!.IsCompleted))
                    .Then(_ => Steps = steps!.Result)
                    .Then(_ =>
                    {
                        var options = new JsonSerializerOptions
                        {
                            WriteIndented = true,
                            Converters =
                            {
                                new PathfinderStepConverter(),
                            },
                        };

                        JSON = JsonSerializer.Serialize(Steps, options);
                    })
                    .Then(_ => pathfinder = null);
            });

            return;
        }

        if (StepProcessor.IsRunning || StepProcessor.QueueCount > 0)
        {
            return;
        }

        progress.CommitReached();

        if (stepIndex >= Steps.Count)
        {
            Teardown();
            return;
        }

        var now = Environment.TickCount64;
        ObserveCombat(now);
        ObserveManualMovement(now);
        if (now < nextStepTickAt)
        {
            return;
        }

        nextStepTickAt = now + StepTickIntervalMs;

        var obj = GetValidObjects().FirstOrDefault(o => Vector3.Distance(Player.Position, o.Position) <= 5f);
        if (obj != null)
        {
            StepProcessor.Submit(GetInteractionChain(obj));
        }

        RunCurrentStep();
    }

    private void RunCurrentStep()
    {
        try
        {
            SkipCoveredSteps();
            if (stepIndex >= Steps.Count)
            {
                return;
            }

            ApplyDetour();
            ApplyCarrotDetour();

            // 戰鬥中原地等：不跑步驟（不送導航、不叫坐騎、不詠唱「返回」），5 米內的寶箱照樣開。
            if (combatWait.Holding)
            {
                return;
            }

            var step = CurrentStep;
            startedStep = stepIndex;
            if (Handlers[step.Type]())
            {
                if (step.Type == PathfinderStepType.WalkToNode)
                {
                    // 有送出互動（或上坐騎）就等動作鏈跑完才算跑過，沒有就當場算。
                    progress.Reach(step.NodeId);
                    if (!StepProcessor.IsRunning && StepProcessor.QueueCount <= 0)
                    {
                        progress.CommitReached();
                    }

                    OnNodeStepDone(step.NodeId);
                }

                stepIndex++;
            }
        }
        catch (Exception ex)
        {
            if (stepErrorIndex == stepIndex && stepErrorType == ex.GetType())
            {
                Svc.Log.Debug($"[BOCCHI][自動化] 「{AutomationControl.GetName(Kind)}」第 {stepIndex + 1} 步又擲出 {ex.GetType().Name}：{ex.Message}");
            }
            else
            {
                stepErrorIndex = stepIndex;
                stepErrorType = ex.GetType();
                Svc.Log.Error(ex, $"[BOCCHI][自動化] 「{AutomationControl.GetName(Kind)}」第 {stepIndex + 1} 步處理時擲出例外，下一輪再試（同一步同一種例外之後只寫 Debug）。");
            }
        }
    }

    // 跳過已確認的點（規則見 HuntStepRules.TrySkip）；正在走向被跳過的點時先停下導航。
    private void SkipCoveredSteps()
    {
        if (!TracksCoverage)
        {
            return;
        }

        coveredPredicate ??= IsNodeCovered;
        while (HuntStepRules.TrySkip(Steps, stepIndex, startedStep == stepIndex, coveredPredicate, out var next, out var node))
        {
            if (startedStep == stepIndex)
            {
                StopNavigationForNewTarget();
            }

            Svc.Log.Debug($"[BOCCHI][巡迴進度] 「{AutomationControl.GetName(Kind)}」略過已確認的點 {node}（第 {stepIndex + 1}～{next} 步）");
            progress.Reach(node);
            progress.CommitReached();
            stepIndex = next;
            startedStep = -1;
        }
    }

    private void ApplyDetour()
    {
        if (!TryPickDetour(out var node, out var append))
        {
            return;
        }

        if (append)
        {
            Steps.Add(PathfinderStep.WalkToDestination(node));
            return;
        }

        Steps.Insert(stepIndex, PathfinderStep.WalkToDestination(node));
        watchedStep = -1;
        startedStep = -1;
        StopNavigationForNewTarget();
    }

    private void ApplyCarrotDetour()
    {
        if (CurrentStep.Type is not (PathfinderStepType.WalkToNode or PathfinderStepType.WalkToAethernet) || yieldGate.Holding)
        {
            return;
        }

        if (!TryPickCarrot(out var objectId, out var position))
        {
            return;
        }

        Steps.Insert(stepIndex, PathfinderStep.DigCarrot(objectId, position));
        watchedStep = -1;
        startedStep = -1;
        ledgeClimb.Reset();
        StopNavigationForNewTarget();
    }

    // 挖胡蘿蔔那一步結束：移除它，原本那一步回到目前這一步、重新開始計時與送移動。
    private void RemoveCarrotStep()
    {
        if (stepIndex < Steps.Count && Steps[stepIndex].Type == PathfinderStepType.DigCarrot)
        {
            Steps.RemoveAt(stepIndex);
        }

        watchedStep = -1;
        startedStep = -1;
        StopNavigationForNewTarget();
    }

    // 前往失敗的那一步交給子類別決定要不要跳過；跳過就停下導航與這一步的動作鏈、換下一步，巡迴不停止。
    private bool SkipUnreachableStep(string what)
    {
        if (stepIndex >= Steps.Count)
        {
            return false;
        }

        var step = CurrentStep;
        if (step.Type == PathfinderStepType.DigCarrot)
        {
            StepProcessor.Abort();
            RemoveCarrotStep();
            OnCarrotUnreachable(step, what);
            return true;
        }

        if (step.Type != PathfinderStepType.WalkToNode || !TrySkipUnreachableNode(step.NodeId))
        {
            return false;
        }

        // 先換到下一步再碰 vnavmesh：停導航擲例外時也不會每一拍重跳同一步。
        var skipped = stepIndex;
        stepIndex++;
        startedStep = -1;
        watchedStep = -1;
        Svc.Log.Information($"[BOCCHI][尋路] 「{AutomationControl.GetName(Kind)}」第 {skipped + 1} 步（走向點 {step.NodeId}）{what}：跳過這一步，巡迴繼續。角色位置 {Player.Position:F1}");
        StepProcessor.Abort();
        StopNavigationForNewTarget();
        return true;
    }

    // 換目的地：先取消還在算的尋路再停下（算完的舊路會把角色帶回原本的目標），守衛重新計時。
    private void StopNavigationForNewTarget()
    {
        staleMovePending = OwnPathfind.CancelIfOnlyOwn(vnav, OwnMoveDestinations.HasCurrent(TravelIntent.State)) is OwnPathfindCancel.OthersQueued or OwnPathfindCancel.Unreadable;
        vnav.Stop();
        moveGuard.Reset();
    }

    // Lifestream 帶角色走時看到 vnavmesh 在走就中止全部任務。讓位時絕不取消算路（全域的，會連 Lifestream 在算的一起取消）：
    // 只停已經在走的路；BOCCHI 還在算的路算完開走時，讓位期間每拍再停（ObserveManualMovement）。回傳有沒有停下東西。
    private bool StopNavigationForLifestream()
    {
        var stopped = false;
        if (vnav.IsRunning())
        {
            vnav.Stop();
            stopped = true;
        }

        moveGuard.Reset();
        return stopped;
    }

    // 每幀在送出規劃之前呼叫：true＝這一拍先不規劃（詠唱或讀圖還沒結束，起點會變）。
    private bool HoldPlanStart()
    {
        var player = Svc.Objects.LocalPlayer;
        var cast = player == null ? ReturnCastKind.None : ReturnCast.Classify(player.IsCasting, player.CastActionType, player.CastActionId);
        var condition = Svc.Condition;
        var wasWaiting = planGate.IsWaiting;
        var state = planGate.Step(Environment.TickCount64, cast, player != null, condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51],
            out var timedOut, out var waitedMs);
        if (state != PlanStartState.Ready)
        {
            if (!wasWaiting)
            {
                var what = state switch
                {
                    PlanStartState.Casting => cast == ReturnCastKind.Return ? "「返回」詠唱" : "詠唱",
                    PlanStartState.AfterReturn => "「返回」之後的讀圖",
                    _ => "讀圖",
                };
                Svc.Log.Information($"[BOCCHI][尋路] 「{AutomationControl.GetName(Kind)}」規劃前先等{what}結束再取起點（最多 {PlanStartGate.MaxWaitMs / 1000} 秒）；角色位置 {Player.Position:F1}");
            }

            return true;
        }

        if (wasWaiting)
        {
            Svc.Log.Information($"[BOCCHI][尋路] 「{AutomationControl.GetName(Kind)}」等了 {waitedMs / 1000f:F1} 秒{(timedOut ? "（到上限）" : "")}，從 {Player.Position:F1} 開始規劃");
        }

        return false;
    }

    // 有優先點才量：第一段要比步行與傳送。逆向沒有優先點時第一站是另一端，一樣先量；只巡部分路線時正向也量（候選＝全部要跑的點）。
    private StartLegSurvey? BeginStartLegSurvey(List<uint> valid, List<uint>? priority)
    {
        if (priority is not { Count: > 0 })
        {
            if (!roundReverse && HasRoundFilter && valid.Count > 0 && pathfinder is BasePathfinder)
            {
                var all = new List<(uint Node, Vector3 Position)>(valid.Count);
                foreach (var node in valid)
                {
                    if (TryGetNodePosition(node, out var nodeAt))
                    {
                        all.Add((node, nodeAt));
                    }
                }

                return all.Count == 0 ? null : StartLegSurvey.Begin(vnav, Player.Position, all, Environment.TickCount64);
            }

            if (!roundReverse || valid.Count == 0 || pathfinder is not BasePathfinder based)
            {
                return null;
            }

            var head = based.ReverseStartNode(Player.Position, valid);
            return TryGetNodePosition(head, out var at) ? StartLegSurvey.Begin(vnav, Player.Position, [(head, at)], Environment.TickCount64) : null;
        }

        var candidates = new List<(uint Node, Vector3 Position)>();
        foreach (var node in valid)
        {
            if (priority.Contains(node) && TryGetNodePosition(node, out var position))
            {
                candidates.Add((node, position));
            }
        }

        return candidates.Count == 0 ? null : StartLegSurvey.Begin(vnav, Player.Position, candidates, Environment.TickCount64);
    }

    // vnavmesh 一筆都沒回答時照原本的規劃（第一站＝直線最近的優先點、直接走過去）。
    private Task<List<PathfinderStep>> PlanPath(List<uint> valid, List<uint>? priority, StartLegSurvey? survey)
    {
        if (roundReverse && pathfinder is BasePathfinder based)
        {
            return PlanReversed(based, valid, priority ?? [], survey);
        }

        if (priority is not { Count: > 0 })
        {
            // 只有「只巡部分路線」才會在沒有優先點的正向量第一段；量不到的點用直線照樣比走法（同逆向）。
            if (survey != null && pathfinder is BasePathfinder routed)
            {
                return routed.FindPathFromStartLeg(survey.Start, valid, survey.Build(out _));
            }

            return pathfinder!.FindPath(Player.Position, valid);
        }

        if (survey == null)
        {
            return pathfinder!.FindPath(Player.Position, valid, priority);
        }

        var costs = survey.Build(out var answered);
        if (answered > 0)
        {
            return pathfinder!.FindPath(survey.Start, valid, priority, costs);
        }

        Svc.Log.Information($"[BOCCHI][尋路] 「{AutomationControl.GetName(Kind)}」第一段：vnavmesh 沒有回答任何步行路長（{survey.NotSent ?? $"{StartLegSurvey.TimeLimitMs / 1000} 秒內沒算完或都失敗"}），"
                            + "照原本的走法：直接走到直線最近的已記錄寶箱");
        return pathfinder!.FindPath(survey.Start, valid, priority);
    }

    // 有優先點時第一段照正向的規則（vnavmesh 沒回答就退回直線最近、直接步行）；沒有優先點時量不到也照直線比走法。
    private Task<List<PathfinderStep>> PlanReversed(BasePathfinder based, List<uint> valid, List<uint> priority, StartLegSurvey? survey)
    {
        var first = valid.Count(priority.Contains);
        StartLegCosts? costs = null;
        if (survey != null)
        {
            costs = survey.Build(out var answered);
            if (answered == 0 && first > 0)
            {
                costs = null;
                Svc.Log.Information($"[BOCCHI][尋路] 「{AutomationControl.GetName(Kind)}」第一段：vnavmesh 沒有回答任何步行路長（{survey.NotSent ?? $"{StartLegSurvey.TimeLimitMs / 1000} 秒內沒算完或都失敗"}），"
                                    + "照原本的走法：直接走到直線最近的已記錄寶箱");
            }
        }

        Svc.Log.Information($"[BOCCHI][尋路] 「{AutomationControl.GetName(Kind)}」這一輪逆向：已記錄的寶箱 {first} 個照正向的規則排，其餘 {valid.Count - first} 個點從正向規劃的另一端開始倒過來走（再照倒過來的方向做局部搜尋）");
        return based.FindPathReversed(survey?.Start ?? Player.Position, valid, priority, costs);
    }

    private static bool IsTravelStep(PathfinderStepType type)
    {
        return type is PathfinderStepType.WalkToNode or PathfinderStepType.WalkToAethernet or PathfinderStepType.ReturnToBaseCamp or PathfinderStepType.DigCarrot;
    }

    /// <summary>上下坐騎、詠唱、跳躍、讀圖中：導航停著是暫時的，不判斷手動移動（同非法模式趕路監看）。</summary>
    private static bool IsMovementPaused()
    {
        var condition = Svc.Condition;
        return condition[ConditionFlag.Mounting] || condition[ConditionFlag.Mounting71] || condition[ConditionFlag.MountOrOrnamentTransition]
               || condition[ConditionFlag.Casting] || condition[ConditionFlag.Casting87]
               || condition[ConditionFlag.Jumping] || condition[ConditionFlag.Jumping61]
               || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51];
    }

    // 每幀（步驟處理器沒在跑時）呼叫；只讀戰鬥與騎乘旗標。等待中暫停「同一步太久」的計時（同讓位）。
    private void ObserveCombat(long now)
    {
        var inCombat = Svc.Condition[ConditionFlag.InCombat];
        if (inCombat)
        {
            lastCombatSeenAt = now;
        }

        var step = combatWait.Observe(inCombat, Svc.Condition[ConditionFlag.Mounted], now);
        watchedStepSince += step.HeldMs;

        if (step.Event == HuntCombatEvent.None)
        {
            return;
        }

        var name = AutomationControl.GetName(Kind);
        var at = $"第 {stepIndex + 1} 步（{CurrentStep.Type}）";
        switch (step.Event)
        {
            case HuntCombatEvent.Started:
                var stopped = StopNavigationForCombat(now);
                ResetYield();
                ledgeClimb.Reset();
                ResetShardStairs();
                Svc.Log.Information($"[BOCCHI][戰鬥等待] 「{name}」{at}進入戰鬥：{stopped}原地等戰鬥結束（讓戰鬥 AI 或你自己打完；這段期間不送導航、不叫坐騎、不判斷手動移動），"
                                    + $"脫離戰鬥 {HuntCombatWait.SettleMs / 1000f:F1} 秒後從目前位置接續；最多等 {HuntCombatWait.MaxWaitMs / 1000} 秒。角色位置 {Player.Position:F1}{(Player.Mounted ? "（騎乘中）" : string.Empty)}");
                break;
            case HuntCombatEvent.Ended:
                ResetYield();
                moveGuard.Reset();
                Svc.Log.Information($"[BOCCHI][戰鬥等待] 「{name}」脫離戰鬥 {HuntCombatWait.SettleMs / 1000f:F1} 秒：這次等了 {step.WaitedMs / 1000f:F1} 秒，從目前位置 {Player.Position:F1} 接續{at}");
                break;
            case HuntCombatEvent.Expired:
                ResetYield();
                moveGuard.Reset();
                Svc.Log.Information($"[BOCCHI][戰鬥等待] 「{name}」{at}等了 {HuntCombatWait.MaxWaitMs / 1000} 秒還在戰鬥：這場戰鬥退回原本的做法（照常送導航"
                                    + $"{(CurrentStep.Type == PathfinderStepType.ReturnToBaseCamp ? "，返回這一步照舊往營地跑" : string.Empty)}；手動移動照常讓位），脫離戰鬥後下一場再原地等。角色位置 {Player.Position:F1}");
                break;
            case HuntCombatEvent.ExpiredOver:
                Svc.Log.Information($"[BOCCHI][戰鬥等待] 「{name}」退回原本做法的那場戰鬥結束了（從開始等算起 {step.WaitedMs / 1000f:F1} 秒），之後再進入戰鬥照樣原地等");
                break;
            case HuntCombatEvent.Riding:
                Svc.Log.Information($"[BOCCHI][戰鬥等待] 「{name}」{at}騎乘中進入戰鬥：繼續走，不原地等（騎著不會出招，騎走會脫離戰鬥）；還在戰鬥時下了坐騎才原地等。角色位置 {Player.Position:F1}");
                break;
        }
    }

    // 回傳給記錄用的前綴。Lifestream 在執行任務（你在地圖上點了目的地）時不停：那是你自己要的移動。
    private string StopNavigationForCombat(long now)
    {
        if (yieldGate.Manual.External || lifestreamBusy?.Query(now) == true)
        {
            return "Lifestream 正在執行任務，不停它；";
        }

        if (!vnav.IsRunning() && !vnav.IsSimpleMoveInProgress())
        {
            moveGuard.Reset();
            return string.Empty;
        }

        StopNavigationForNewTarget();
        return "停下 BOCCHI 自己的導航，";
    }

    // 每幀（步驟處理器沒在跑時）呼叫；取樣約每 100 毫秒一次，選項關著時什麼都不讀。讓位中暫停「同一步太久」的計時。
    // 戰鬥中原地等的時候不判斷：那時角色的移動是戰鬥 AI 在打，不是使用者在操作。
    private void ObserveManualMovement(long now)
    {
        yieldGate.Enabled = config.YieldToManualMovement;
        if (!yieldGate.Enabled || stepIndex >= Steps.Count || !IsTravelStep(CurrentStep.Type) || combatWait.Holding)
        {
            if (yieldStep != -1)
            {
                ResetYield();
            }

            return;
        }

        if (yieldStep != stepIndex)
        {
            if (yieldGate.Manual.Expired)
            {
                yieldGate.Reset();
            }

            yieldGate.OnStepChanged();
            yieldStep = stepIndex;
        }

        // 按鍵每拍都讀：短按可能落在兩次取樣之間。不經建構子建出的物件第一次用時才建。
        handsOn ??= new HandsOnProbe();
        var input = handsOn.ReadInput(now);
        if (yieldGate.Manual.External && vnav.IsRunning())
        {
            vnav.Stop();
            Svc.Log.Debug($"[BOCCHI][巡迴讓位] 「{AutomationControl.GetName(Kind)}」為 Lifestream 讓位中 vnavmesh 開始走（BOCCHI 先前送的路算完了）：停下");
        }

        var player = Svc.Objects.LocalPlayer;
        if (!yieldGate.ShouldSample(now) || player == null)
        {
            return;
        }

        var navigating = vnav.IsRunning() || vnav.IsSimpleMoveInProgress();
        var interacting = !input && HandsOnProbe.Interacting(now);
        var step = yieldGate.Observe(player.Position, navigating, !navigating && IsMovementPaused(), lifestreamBusy?.Query(now) == true, input || interacting, now);
        if (step.YieldedMs > 0)
        {
            segmentYieldMs += step.YieldedMs;
            watchedStepSince += step.YieldedMs;
        }

        if (yieldGate.Manual.Yielding)
        {
            HandsOnRecent.Note(now, "巡迴正在讓位給你操作");
        }

        switch (step.Event)
        {
            case TravelYieldEvent.Touched when yieldGate.Manual.Capped:
                ReportYield(ref cappedTouchReported, $"[BOCCHI][巡迴讓位] 「{AutomationControl.GetName(Kind)}」第 {stepIndex + 1} 步讓位滿 {ManualMoveDetector.MaxYieldMs / 1000} 秒後偵測到你還在操作"
                                                     + $"（{(input ? "按了移動鍵" : "在互動：開寶箱、對話一類")}）：不重送，改成按鍵／互動讓位；沒有按鍵、沒在互動、角色靜止 {ManualMoveDetector.ResumeQuietMs / 1000f:F1} 秒後從目前位置接續；"
                                                     + $"這種讓位每一步累計上限 {ManualMoveDetector.TouchMaxMs / 1000} 秒。角色位置 {player.Position:F1}");
                break;
            case TravelYieldEvent.Touched:
                ReportYield(ref touchReported, $"[BOCCHI][巡迴讓位] 「{AutomationControl.GetName(Kind)}」第 {stepIndex + 1} 步（{CurrentStep.Type}）導航停下時偵測到你在操作"
                                               + $"（{(input ? "按了移動鍵" : "在互動：開寶箱、對話一類")}）：不重送；沒有按鍵、沒在互動、角色靜止 {ManualMoveDetector.ResumeQuietMs / 1000f:F1} 秒後從目前位置接續；"
                                               + $"這種讓位每一步累計上限 {ManualMoveDetector.TouchMaxMs / 1000} 秒。角色位置 {player.Position:F1}");
                break;
            case TravelYieldEvent.TouchLimit:
                Svc.Log.Information($"[BOCCHI][巡迴讓位] 「{AutomationControl.GetName(Kind)}」第 {stepIndex + 1} 步為按鍵／互動讓位累計超過 {ManualMoveDetector.TouchMaxMs / 1000} 秒："
                                    + $"這一步之後只看持續移動，停下就照常重送。角色位置 {player.Position:F1}");
                break;
            case TravelYieldEvent.External:
                var stopped = navigating && StopNavigationForLifestream();
                ReportYield(ref externalReported, $"[BOCCHI][巡迴讓位] 「{AutomationControl.GetName(Kind)}」第 {stepIndex + 1} 步：Lifestream 正在執行任務（多半是你在地圖上點了目的地）："
                                                  + $"{(stopped ? "停下 BOCCHI 自己的導航、" : string.Empty)}讓位到它做完，這段時間不算進 {ManualMoveDetector.MaxYieldMs / 1000} 秒上限；"
                                                  + $"它做完、角色靜止 {ManualMoveDetector.ResumeQuietMs / 1000f:F1} 秒後從目前位置接續。角色位置 {player.Position:F1}");
                break;
            case TravelYieldEvent.ExternalLimit:
                Svc.Log.Information($"[BOCCHI][巡迴讓位] 「{AutomationControl.GetName(Kind)}」第 {stepIndex + 1} 步：為 Lifestream 讓位累計超過 {ManualMoveDetector.ExternalMaxMs / 1000} 秒，"
                                    + $"之後不再為它讓位（照手動移動的規則）。角色位置 {player.Position:F1}");
                break;
            case TravelYieldEvent.Started:
                ReportYield(ref yieldReported, $"[BOCCHI][巡迴讓位] 「{AutomationControl.GetName(Kind)}」第 {stepIndex + 1} 步（{CurrentStep.Type}）偵測到手動移動"
                                               + $"（導航停著、角色以 {step.Speed:F1} 米/秒持續移動 {ManualMoveDetector.ConfirmMs / 1000f:F1} 秒）：暫停自動前往，"
                                               + $"角色靜止 {ManualMoveDetector.ResumeQuietMs / 1000f:F1} 秒後從目前位置接續；超過 {ManualMoveDetector.MaxYieldMs / 1000} 秒這一步改回照常重送。角色位置 {player.Position:F1}");
                break;
            case TravelYieldEvent.Expired:
                Svc.Log.Information($"[BOCCHI][巡迴讓位] 「{AutomationControl.GetName(Kind)}」第 {stepIndex + 1} 步手動移動超過 {ManualMoveDetector.MaxYieldMs / 1000} 秒：這一步改回照常重送。角色位置 {player.Position:F1}");
                break;
            case TravelYieldEvent.Cancelled:
                Svc.Log.Debug($"[BOCCHI][巡迴讓位] 「{AutomationControl.GetName(Kind)}」讓位中導航又開始走：結束讓位、不另外接續");
                break;
        }
    }

    // 同一段第一次寫 Information，之後寫 Debug。
    private static void ReportYield(ref bool reported, string line)
    {
        if (reported)
        {
            Svc.Log.Debug(line);
            return;
        }

        reported = true;
        Svc.Log.Information(line);
    }

    // 開始／停止在主視窗最上方的自動化控制；這裡只在跑過或正在跑時顯示經過時間、匯出與進度。
    public void Draw(Module<Plugin, Config> module)
    {
        var elapsed = stopwatch.Elapsed;
        var hasElapsed = elapsed > TimeSpan.Zero;
        var showProgress = running && Steps.Count > 0;
        var showExtra = HasExtraRows;
        if (!hasElapsed && !showProgress && !showExtra)
        {
            return;
        }

        MainPanelUi.Title(module.T("panel.hunt.title"), hasElapsed);
        if (hasElapsed)
        {
            ImGui.SameLine();
            MainPanelUi.Labelled(I18N.T("hunter.elapsed"), $"{elapsed:mm\\:ss}");
            ImGui.SameLine();
            if (ImGui.Button(I18N.T("hunter.export.label")))
            {
                ClipboardService.SetText(JSON);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(I18N.T("hunter.export.tooltip"));
            }
        }

        DrawRange(module);

        if (showProgress)
        {
            DrawProgress(module);
        }

        if (showExtra)
        {
            DrawExtraRows(module);
        }
    }

    private void DrawProgress(Module<Plugin, Config> module)
    {
        ImGui.Indent(16);
        MainPanelUi.Labelled(I18N.T("hunter.progress"), $"{stepIndex}/{Steps.Count}");

        // 最後一步做完到下一拍收尾之間 stepIndex 等於步數，這時沒有目前這一步。
        if (stepIndex < Steps.Count)
        {
            var step = CurrentStep.Type;
            if (step == PathfinderStepType.WalkToNode)
            {
                MainPanelUi.Gap();
                MainPanelUi.Labelled(module.T("panel.hunt.distance_node"), $"{distance:f2}/{GetDetectionRange():f2}");
            }
            else if (step == PathfinderStepType.WalkToAethernet)
            {
                MainPanelUi.Gap();
                MainPanelUi.Labelled(I18N.T("hunter.distance_shard"), $"{distance:f2}");
            }
        }

        ImGui.Unindent(16);
    }

    // 「本輪 範圍內／全部 點（上限與來源）」；還沒規劃畫灰色 ?，分母與來源的說明放說明框。
    private void DrawRange(Module<Plugin, Config> module)
    {
        ImGui.Indent(16);
        var label = I18N.T("hunter.range.label");
        if (range.Total <= 0)
        {
            ImGui.TextColored(OcelotColor.Yellow, $"{label}:");
            ImGui.SameLine();
            ImGui.TextColored(UnknownColor, "?");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(I18N.T("hunter.range.unknown_tooltip"));
            }
        }
        else
        {
            RefreshRangeText(module);
            MainPanelUi.Labelled(label, rangeText);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(rangeTooltip);
            }
        }

        ImGui.Unindent(16);
    }

    private void RefreshRangeText(Module<Plugin, Config> module)
    {
        var template = I18N.T("hunter.range.value");
        if (rangeTextVersion == rangeVersion && ReferenceEquals(template, rangeTemplate))
        {
            return;
        }

        rangeTextVersion = rangeVersion;
        rangeTemplate = template;
        rangeText = template
            .Replace("{in}", range.InRange.ToString())
            .Replace("{total}", range.Total.ToString())
            .Replace("{max}", range.MaxLevel.ToString())
            .Replace("{source}", I18N.T(SourceKey(range.Source)));
        if (HasDirection)
        {
            rangeText += I18N.T("hunter.range.direction").Replace("{direction}", I18N.T(roundReverse ? "hunter.direction.reverse" : "hunter.direction.forward"));
        }

        rangeText += RoundRangeSuffix(module);
        rangeTooltip = module.T("panel.hunt.range.tooltip")
            .Replace("{cap}", KnowledgeLevel.SouthHornCap.ToString())
            .Replace("{ceiling}", PathfinderConfig.LevelCeiling.ToString());
    }

    private static string SourceKey(MaxLevelSource source)
    {
        return source switch
        {
            MaxLevelSource.FollowKnowledge => "hunter.range.source.follow",
            MaxLevelSource.KnowledgeAtCap => "hunter.range.source.at_cap",
            MaxLevelSource.KnowledgeUnavailable => "hunter.range.source.unavailable",
            _ => "hunter.range.source.manual",
        };
    }

    private void SetRange(HuntRange next)
    {
        range = next;
        rangeVersion++;
    }

    // 只有跟隨知見等級時才讀知見等級（規劃時一次，不是每幀）。
    private MaxLevelChoice ResolveMaxLevel()
    {
        var knowledge = config.FollowKnowledgeLevel ? KnowledgeLevel.Read() : default;
        var choice = PathfinderConfig.ChooseMaxLevel(config.FollowKnowledgeLevel, config.MaxLevel, knowledge);
        Svc.Log.Information(choice.Source switch
        {
            MaxLevelSource.FollowKnowledge => $"[BOCCHI][尋路] 區域等級上限 {choice.Level}（跟隨真實知見等級 {knowledge.Real}）",
            MaxLevelSource.KnowledgeAtCap => $"[BOCCHI][尋路] 區域等級上限 {choice.Level}（真實知見等級 {knowledge.Real} 已滿級（上限 {KnowledgeLevel.SouthHornCap}），改用設定值）",
            MaxLevelSource.KnowledgeUnavailable => $"[BOCCHI][尋路] 區域等級上限 {choice.Level}（讀不到知見等級，沿用設定值）",
            _ => $"[BOCCHI][尋路] 區域等級上限 {choice.Level}（設定值，沒有跟隨知見等級）",
        });
        return choice;
    }

    // 這一輪要跑的點。續跑時用現在的等級上限重新取點，再扣掉已經跑過的；TracksCoverage 的再交給 AdjustPlan（拿到的是不分路線的範圍）。
    private List<uint> PlanNodes()
    {
        var choice = ResolveMaxLevel();
        var inRange = GetValidNodes(choice.Level);
        var routed = HasRoundFilter ? inRange.FindAll(IsInRound) : inRange;
        var total = GetValidNodes(PathfinderConfig.LevelCeiling);
        SetRange(new HuntRange(choice.Level, choice.Source, routed.Count, HasRoundFilter ? total.Count(IsInRound) : total.Count));
        var valid = routed;
        if (resumePlan)
        {
            resumePlan = false;
            valid = progress.Remaining(routed);
            Svc.Log.Information($"[BOCCHI][巡迴進度] 續跑「{AutomationControl.GetName(Kind)}」重新規劃：範圍內 {routed.Count} 個點，略過已跑過的 {routed.Count - valid.Count} 個，"
                                + $"剩 {valid.Count} 個，從角色目前位置 {Player.Position:F1} 出發");
        }

        if (TracksCoverage)
        {
            planPriority ??= [];
            planPriority.Clear();
            if (ReferenceEquals(valid, inRange))
            {
                valid = new List<uint>(inRange);
            }

            AdjustPlan(inRange, valid, planPriority);
        }

        progress.AddPlanned(valid);
        return valid;
    }

    private void FinishEmptyRoute()
    {
        EndSegment(HuntEnd.Empty, null, HuntSegment.ProgressLabel(HuntEnd.Empty, false, false));
        Svc.Log.Information($"[BOCCHI][巡迴進度] 「{AutomationControl.GetName(Kind)}」沒有要跑的點（已跑 {ProgressText} 點；範圍內的點都跑過了，或區域等級上限內沒有點），已結束。");
        stopwatch.Stop();
        running = false;
        stepIndex = 0;
        Steps.Clear();
        pathfinder = null;
        routeEmpty = false;
        progress.Clear();
    }

    private bool IsStalled()
    {
        if (Steps.Count <= 0 || stepIndex >= Steps.Count)
        {
            watchedStep = -1;
            return false;
        }

        var now = Environment.TickCount64;
        if (stepIndex != watchedStep)
        {
            watchedStep = stepIndex;
            watchedStepSince = now;
            return false;
        }

        return now - watchedStepSince > StepStallLimitMs;
    }

    protected virtual void Teardown()
    {
        EndSegment(HuntEnd.Finished, null, HuntSegment.ProgressLabel(HuntEnd.Finished, false, false));
        Svc.Log.Information($"[BOCCHI][自動化] 「{AutomationControl.GetName(Kind)}」走完全部 {Steps.Count} 步，已結束。");
        stopwatch.Stop();
        running = false;
        stepIndex = 0;
        Steps.Clear();
        vnav.Stop();
        Plugin.Chain.Abort();
        StepProcessor.Abort();
        pathfinder = null;
        progress.Clear();
    }


    protected void BeginSegment(string how)
    {
        segmentStartedAt = Environment.TickCount64;
        segmentStart = how;
        segmentVisitedAtStart = progress.VisitedCount;
        bronzeAtStart = ChestsOpenedSoFar(TreasureType.Bronze);
        silverAtStart = ChestsOpenedSoFar(TreasureType.Silver);
        bunnyOpened = 0;
        segmentYieldMs = 0;
        yieldReported = resumeReported = externalReported = touchReported = cappedTouchReported = false;
    }

    /// <summary>寫這一段的 [寶箱巡迴] 結束行（每段一行）；沒有進行中的一段時不寫。</summary>
    protected void EndSegment(HuntEnd end, string? reason, string progressState)
    {
        if (segmentStartedAt == 0)
        {
            return;
        }

        var seconds = (Environment.TickCount64 - segmentStartedAt) / 1000.0;
        segmentStartedAt = 0;
        try
        {
            WriteSegmentLine(end, reason, progressState, seconds);
        }
        catch (Exception ex)
        {
            // 這裡在停止流程裡：記錄出錯不可以擋住停止。
            Svc.Log.Warning($"[BOCCHI][寶箱巡迴] 寫結束行時出錯：{ex.GetType().Name}：{ex.Message}");
        }
    }

    private void WriteSegmentLine(HuntEnd end, string? reason, string progressState, double seconds)
    {
        var treasure = Kind == AutomationKind.TreasureHunt;
        Svc.Log.Information(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[BOCCHI][寶箱巡迴] 結束 種類={Kind.CommandKey()} 開始={segmentStart} 結束={HuntSegment.Label(end)} 原因=「{reason ?? "-"}」 規劃={(progress.PlannedCount > 0 ? progress.PlannedCount.ToString() : "?")} "
            + $"走到={progress.VisitedCount} 本段={progress.VisitedCount - segmentVisitedAtStart} 銅箱={(treasure ? (ChestsOpenedSoFar(TreasureType.Bronze) - bronzeAtStart).ToString() : "-")} 銀箱={(treasure ? (ChestsOpenedSoFar(TreasureType.Silver) - silverAtStart).ToString() : "-")} "
            + $"兔子寶箱={(treasure ? "-" : bunnyOpened.ToString())} 耗時={seconds:F1} 進度={progressState} 讓位={segmentYieldMs / 1000.0:F1} "
            + $"方向={(!HasDirection ? "-" : roundReverse ? "逆向" : "正向")} 路線={RoundRouteLabel}"));
    }

    /// <summary>到現在為止開了幾個這種寶箱的累計數（[寶箱巡迴] 結束行取一段前後的差）；只在 framework 執行緒呼叫。</summary>
    protected virtual int ChestsOpenedSoFar(TreasureType type)
    {
        return 0;
    }

    /// <summary>幸運胡蘿蔔巡迴對兔子寶箱送出了互動。</summary>
    protected void NoteBunnyChest()
    {
        bunnyOpened++;
    }

    /// <summary>
    /// 走向 <paramref name="destination"/>：在走或還在算路時不重送（見 <see cref="MoveRequestGuard"/>）。
    /// 回 false＝同一個目的地太久都沒開始走：子類別讓這一點跳過時已經換到下一步，否則已寫記錄並停止巡迴（進度保留成暫停）；
    /// 呼叫端都要立刻 return。
    /// </summary>
    private bool RequestMove(Vector3 destination, TravelSource? source)
    {
        var moving = vnav.IsRunning();
        var computing = vnav.IsSimpleMoveInProgress();
        if (staleMovePending && (moving || !computing))
        {
            staleMovePending = false;
            if (moving)
            {
                vnav.Stop();
                moving = false;
                moveGuard.Reset();
                Svc.Log.Debug($"[BOCCHI][尋路] 「{AutomationControl.GetName(Kind)}」換目的地前沒取消掉的舊算路算完開走了：停下，改送 {destination:F1}");
            }
        }

        holdingForManual = false;
        switch (yieldGate.Next(moving || computing))
        {
            case HuntMoveDecision.Hold:
                holdingForManual = true;
                return true;
            case HuntMoveDecision.Resume:
                moveGuard.Reset();
                ReportYield(ref resumeReported, $"[BOCCHI][巡迴讓位] 「{AutomationControl.GetName(Kind)}」第 {stepIndex + 1} 步角色已靜止 {ManualMoveDetector.ResumeQuietMs / 1000f:F1} 秒："
                                                + $"從目前位置 {Player.Position:F1} 接續前往 {destination:F1}（這一段累計讓位 {segmentYieldMs / 1000f:F1} 秒）");
                break;
        }

        var decision = moveGuard.Next(destination, Player.Position, moving, computing, vnav.IsNavmeshReady(), Environment.TickCount64);
        if (decision == MoveRequestDecision.Send)
        {
            if (source is { } travel)
            {
                TravelIntent.Register(destination, travel);
            }
            else
            {
                // 沒登記趕路的移動（戰鬥中跑回營地）也記下，停止後的導航守衛才認得是自己的。
                OwnMoveDestinations.Note(destination);
            }

            vnav.PathfindAndMoveTo(destination, false);
            return true;
        }

        if (decision != MoveRequestDecision.GiveUp)
        {
            return true;
        }

        var seconds = MoveRequestGuard.NoStartLimitMs / 1000;
        if (SkipUnreachableStep($"走向 {destination:F1}：{seconds} 秒內都沒開始移動（送了 {moveGuard.Sends} 次）"))
        {
            return false;
        }

        Svc.Log.Information($"[BOCCHI][尋路] 「{AutomationControl.GetName(Kind)}」第 {stepIndex + 1} 步（{CurrentStep.Type}）走向 {destination:F1}：{seconds} 秒內都沒開始移動"
                            + $"（vnavmesh 算不出路或一直失敗，這段期間送了 {moveGuard.Sends} 次），不再重送，停止巡迴。角色位置 {Player.Position:F1}");
        var reason = I18N.T("automation.reason.no_path", new Dictionary<string, string>
        {
            ["step"] = (stepIndex + 1).ToString(),
            ["seconds"] = seconds.ToString(),
        });
        plugin.Automation.Stop(Kind, reason, true, true);
        return false;
    }

    protected bool WalkToNodeHandler()
    {
        var destination = GetDestinationForCurrentStep();
        if (TryGetLedge(CurrentStep.NodeId, out var ledge) && ClimbLedge(destination, ledge) is { } climbed)
        {
            return climbed;
        }

        if (!RequestMove(destination, TravelSource.HuntNode))
        {
            return false;
        }

        if (!Player.Mounted && !holdingForManual)
        {
            StepProcessor.SubmitFront(ChainHelper.MountChain());
        }

        distance = Player.DistanceTo(destination);
        if (distance <= GetDetectionRange())
        {
            var obj = GetValidObjects().FirstOrDefault(o => Vector3.Distance(destination, o.Position) <= 5f);

            if (obj == null)
            {
                vnav.Stop();
                OnNodeAbsent(CurrentStep.NodeId);
                return true;
            }

            if (distance <= DISTANCE_TO_NODE_TO_USE)
            {
                StepProcessor.SubmitFront(GetInteractionChain(obj));
                return true;
            }
        }

        return distance <= DISTANCE_TO_NODE_TO_USE;
    }

    // 跳上低台的那幾拍（規則見 LedgeClimb）；回 null＝站在台上了，照常走到點。附近確定沒有寶箱就照常判「那裡沒有」，不跳。
    private bool? ClimbLedge(Vector3 destination, HuntLedge ledge)
    {
        var player = Player.Position;
        var node = CurrentStep.NodeId;
        distance = Vector3.Distance(player, destination);
        var onTop = ledge.IsOnTop(player, destination);
        if (!onTop && distance <= GetDetectionRange() && !GetValidObjects().Any(o => Vector3.Distance(destination, o.Position) <= 5f))
        {
            ledgeClimb.Reset();
            vnav.Stop();
            OnNodeAbsent(node);
            return true;
        }

        var now = Environment.TickCount64;
        var action = ledgeClimb.Step(now, stepIndex, player, onTop, Svc.Condition[ConditionFlag.Jumping], yieldGate.Holding, ledge);
        if (action == LedgeAction.Jump)
        {
            var sent = TryJump();
            Svc.Log.Debug($"[BOCCHI][跳上低台] 「{AutomationControl.GetName(Kind)}」點 {node} 第 {ledgeClimb.Attempts + 1} 次：{(sent ? "跳" : "跳躍送不出去")}；角色位置 {player:F2}");
            action = ledgeClimb.JumpSent(sent, now);
        }

        switch (action)
        {
            case LedgeAction.Proceed:
                return null;
            case LedgeAction.Approach:
                if (!RequestMove(ledge.Approach, TravelSource.HuntNode))
                {
                    return false;
                }

                if (!Player.Mounted && !holdingForManual)
                {
                    StepProcessor.SubmitFront(ChainHelper.MountChain());
                }

                return false;
            case LedgeAction.Stop:
                StopNavigationForNewTarget();
                if (ledgeClimb.Attempts == 0)
                {
                    Svc.Log.Information($"[BOCCHI][跳上低台] 「{AutomationControl.GetName(Kind)}」點 {node} 的寶箱在低台上：到了起跳點 {ledge.Approach:F1}，停下後往 {ledge.Landing:F1} 直線前進並跳躍"
                                        + $"（最多試 {LedgeClimb.MaxAttempts} 次）；角色位置 {player:F2}");
                }

                return false;
            case LedgeAction.RunUp:
                OwnMoveDestinations.Note(ledge.Landing);
                vnav.MoveTo([ledge.Landing], false);
                return false;
            case LedgeAction.Landed:
                StopNavigationForNewTarget();
                Svc.Log.Information($"[BOCCHI][跳上低台] 「{AutomationControl.GetName(Kind)}」點 {node}：第 {ledgeClimb.Attempts + 1} 次站上低台，照常走到寶箱；角色位置 {player:F2}");
                return null;
            case LedgeAction.Retry:
                StopNavigationForNewTarget();
                Svc.Log.Information($"[BOCCHI][跳上低台] 「{AutomationControl.GetName(Kind)}」點 {node} 第 {ledgeClimb.Attempts} 次沒上去（{ledgeClimb.LastFailure}），回起跳點重來；角色位置 {player:F2}");
                return false;
            case LedgeAction.GiveUp:
                StopNavigationForNewTarget();
                var what = $"跳上低台試了 {ledgeClimb.Attempts} 次都沒上去（最後一次：{ledgeClimb.LastFailure}）";
                if (SkipUnreachableStep(what))
                {
                    return false;
                }

                // 沒有已記錄的寶箱可記成走不到：這一輪當作跑過這一點，不停止巡迴。
                Svc.Log.Information($"[BOCCHI][跳上低台] 「{AutomationControl.GetName(Kind)}」點 {node} {what}：這一輪跳過這一點，巡迴繼續。角色位置 {player:F2}");
                return true;
            default:
                return false;
        }
    }

    // 跳躍＝GeneralAction 2（台服 GeneralAction 表）；位址解不出來時不呼叫（同 vnavmesh 跟隨路徑的跳躍）。
    internal static unsafe bool TryJump()
    {
        if (ActionManager.Addresses.Instance.Value == 0 || ActionManager.Addresses.UseAction.Value == 0)
        {
            return false;
        }

        var manager = ActionManager.Instance();
        return manager != null && manager->UseAction(ActionType.GeneralAction, JumpActionId);
    }

    private bool ReturnToBaseCampHandler()
    {
        distance = 0;
        var now = Environment.TickCount64;
        var inCombat = states.GetState() == State.InCombat;
        if (inCombat)
        {
            lastCombatSeenAt = now;
        }

        // 戰鬥中（含旗標剛掉、還沒滿脫戰時間）往營地跑脫戰；連打下一隻時旗標會短暫掉下，單一拍的「不在戰鬥」不算。
        if (!HuntCombatWait.IsSettled(inCombat, lastCombatSeenAt, now))
        {
            RequestMove(Aethernet.BaseCamp.GetData().Position, null);
            return false;
        }

        if (lastCombatSeenAt != 0 && now - lastCombatSeenAt < 10_000)
        {
            Svc.Log.Information($"[BOCCHI][戰鬥等待] 「{AutomationControl.GetName(Kind)}」回營地這一步：脫離戰鬥 {(now - lastCombatSeenAt) / 1000f:F1} 秒，"
                                + $"停下往營地跑的導航（含還在算的尋路），改施放返回。角色位置 {Player.Position:F1}");
        }

        StopNavigationForNewTarget();

        StepProcessor.SubmitFront(ChainHelper.ReturnChain(new ReturnChainConfig
        {
            ApproachAetheryte = true,
        }));

        return true;
    }

    private bool WalkToAethernetHandler()
    {
        var aethernet = CurrentStep.Aethernet;
        var destination = aethernet.GetData().Position;
        if (ClimbShardStairs(aethernet, destination) is { } climbing)
        {
            return climbing;
        }

        if (!RequestMove(destination, TravelSource.HuntShard))
        {
            return false;
        }

        if (!Player.Mounted && !holdingForManual)
        {
            StepProcessor.SubmitFront(ChainHelper.MountChain());
        }

        distance = Player.DistanceTo(destination);
        return distance <= 4f;
    }

    private bool TeleportToAethernetHandler()
    {
        distance = 0;
        StepProcessor.SubmitFront(ChainHelper.TeleportChain(CurrentStep.Aethernet));
        return true;
    }

    // 台階處理用的哪一組、屬於哪一步、走向哪一座；shardStairsDoneStep＝這一步已經處理完（上去了、本來就在台上或放棄），不再接手。
    // 步驟編號每次巡迴從 0 重算：開始／續跑／重設時一起清掉（ResetShardStairs）。
    private HuntLedge? shardStairs;

    private Aethernet shardStairsFor;

    private int shardStairsStep = -1;

    private int shardStairsDoneStep = -1;

    private void ResetShardStairs()
    {
        shardStairs = null;
        shardStairsStep = -1;
        shardStairsDoneStep = -1;
    }

    // 晶體在台上、vnavmesh 常卡在台階下（TravelRoutes.ShardStairs）：夠近又在台階下時照 LedgeClimb 走到起跳點跳上去。回 null＝照常走。
    private bool? ClimbShardStairs(Aethernet aethernet, Vector3 shard)
    {
        if (shardStairsDoneStep == stepIndex && shardStairsFor == aethernet)
        {
            return null;
        }

        var player = Player.Position;
        if (shardStairs == null || shardStairsStep != stepIndex || shardStairsFor != aethernet)
        {
            if (!TravelRoutes.TryPickShardStairs(aethernet, shard, player, out var picked))
            {
                return null;
            }

            shardStairs = picked;
            shardStairsStep = stepIndex;
            shardStairsFor = aethernet;
            Svc.Log.Information($"[BOCCHI][階梯跳躍] 「{AutomationControl.GetName(Kind)}」走向乙太之晶「{aethernet.ToFriendlyString()}」，角色在台階下 {player:F1}："
                                + $"先到起跳點 {picked.Approach:F1}、往 {picked.Landing:F1} 跳上台（最多 {LedgeClimb.MaxAttempts} 次）");
        }

        var ledge = shardStairs.Value;
        distance = Vector3.Distance(player, shard);
        var now = Environment.TickCount64;
        var action = ledgeClimb.Step(now, stepIndex, player, ledge.IsOnTop(player, shard), Svc.Condition[ConditionFlag.Jumping], yieldGate.Holding, ledge);
        if (action == LedgeAction.Jump)
        {
            var sent = TryJump();
            Svc.Log.Debug($"[BOCCHI][階梯跳躍] 「{AutomationControl.GetName(Kind)}」第 {ledgeClimb.Attempts + 1} 次：{(sent ? "跳" : "跳躍送不出去")}；角色位置 {player:F2}");
            action = ledgeClimb.JumpSent(sent, now);
        }

        switch (action)
        {
            case LedgeAction.Approach:
                RequestMove(ledge.Approach, TravelSource.HuntShard);
                return false;
            case LedgeAction.Stop:
            case LedgeAction.Retry:
                StopNavigationForNewTarget();
                if (action == LedgeAction.Retry)
                {
                    Svc.Log.Information($"[BOCCHI][階梯跳躍] 「{AutomationControl.GetName(Kind)}」第 {ledgeClimb.Attempts} 次沒上去（{ledgeClimb.LastFailure}），回起跳點重來；角色位置 {player:F2}");
                }

                return false;
            case LedgeAction.RunUp:
                OwnMoveDestinations.Note(ledge.Landing);
                vnav.MoveTo([ledge.Landing], false);
                return false;
            case LedgeAction.Landed:
            case LedgeAction.Proceed:
            case LedgeAction.GiveUp:
                StopNavigationForNewTarget();
                shardStairsDoneStep = stepIndex;
                shardStairs = null;
                Svc.Log.Information(action switch
                {
                    LedgeAction.Landed => $"[BOCCHI][階梯跳躍] 「{AutomationControl.GetName(Kind)}」第 {ledgeClimb.Attempts + 1} 次跳上台了，接著走到晶體；角色位置 {player:F2}",
                    LedgeAction.Proceed => $"[BOCCHI][階梯跳躍] 「{AutomationControl.GetName(Kind)}」還沒跳就已經在台上了，照常走到晶體；角色位置 {player:F2}",
                    _ => $"[BOCCHI][階梯跳躍] 「{AutomationControl.GetName(Kind)}」跳了 {ledgeClimb.Attempts} 次都沒上去（最後一次：{ledgeClimb.LastFailure}），照常讓 vnavmesh 走；角色位置 {player:F2}",
                });
                return null;
            default:
                return false;
        }
    }

    // 一律回 false：做完時自己移除這一步（不前進 stepIndex），原本那一步接著跑。
    private bool DigCarrotHandler()
    {
        var step = CurrentStep;
        switch (OnCarrotStep(step))
        {
            case CarrotStepAction.Walk:
                if (!RequestMove(step.Position, TravelSource.HuntNode))
                {
                    return false;
                }

                distance = Player.DistanceTo(step.Position);
                if (!Player.Mounted && !holdingForManual && distance > CarrotMountDistance)
                {
                    StepProcessor.SubmitFront(ChainHelper.MountChain());
                }

                return false;
            case CarrotStepAction.Hold:
                return false;
            default:
                RemoveCarrotStep();
                return false;
        }
    }

    // 胡蘿蔔就在旁邊時不叫坐騎：到了要先下來才能用道具。
    private const float CarrotMountDistance = 20f;
}
