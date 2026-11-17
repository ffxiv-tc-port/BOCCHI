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

public abstract partial class Hunter
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

    // 最後一次 RequestMove 送給 vnavmesh 的目的地（停止點認自己的路線用）；null＝還沒送過。
    private Vector3? lastSentDestination;

    // 只在框架執行緒用；每次停止前重新抓，不跨呼叫保存內容。
    private readonly OwnMoveDestinations stopDestinations = new();

    private readonly ForeignPathNotice newTargetNotice = new();

    private readonly ForeignPathNotice lifestreamNotice = new();

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
        lastSentDestination = null;
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
        lastSentDestination = null;
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
        lastSentDestination = null;
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

    // 台階處理用的哪一組、屬於哪一步、走向哪一座；shardStairsDoneStep＝這一步已經處理完（上去了、本來就在台上或放棄），不再接手。
    // 步驟編號每次巡迴從 0 重算：開始／續跑／重設時一起清掉（ResetShardStairs）。
    private HuntLedge? shardStairs;

    private Aethernet shardStairsFor;

    private int shardStairsStep = -1;

    private int shardStairsDoneStep = -1;

    // 胡蘿蔔就在旁邊時不叫坐騎：到了要先下來才能用道具。
    private const float CarrotMountDistance = 20f;
}
