using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BOCCHI.Automation;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.Pathfinder;
using BOCCHI.Pathfinding;
using BOCCHI.Windows;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.ImGuiMethods;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Ocelot;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;
using Ocelot.Modules;
using NativeTreasure = FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace BOCCHI.Modules.Treasure;

// 本檔與 TreasureModule 的寶箱記錄契約：規劃略過巡過（而且沒有已記錄寶箱）的生成點，已記錄的寶箱排最前面；
// 進行中看到寶箱照 TreasureDetourRules 插隊或排最後，同一個點每一輪只試一次。暫停續跑不限時間；巡過與這一輪的已跑點
// 只在手動清除、換區、登出時清（參與 F.A.T.E.／緊急遭遇戰不清）。
// 前往失敗的已記錄寶箱記成走不到（同樣到清除或換區才清），刻意不去的生成點（TreasureData.Avoided）只記錄不排、不插隊。
public partial class TreasureHunt(TreasureModule module) : Hunter(module)
{
    // 圖示字元先轉好：IconButton(FontAwesomeIcon) 每次呼叫都會新建一個字串。
    private static readonly string ClearIcon = FontAwesomeIcon.Eraser.ToIconString();

    private List<TreasureData.TreasureDatum> Treasure = [];

    // 這一輪規劃時區域等級範圍內的點：範圍外的寶箱不插隊。
    private readonly HashSet<uint> planRange = [];

    // 這一輪已經走過或插隊過的點：開不了的寶箱不會一直繞回去。
    private readonly HashSet<uint> attempted = [];

    // 這一輪不巡的路線與「只開所選路線內的已記錄寶箱」：從頭開始時照設定取一份，續跑沿用；空＝全部巡。
    private readonly HashSet<Aethernet> roundExcluded = [];

    private bool roundChestsInRoutesOnly;

    // 第一次用到才建（同 RouteMenu）。
    private CarrotDetour? carrotDetour;

    // 主視窗「順路挖幸運胡蘿蔔」勾選框的標籤與說明：語言或距離上限變了才重組。
    private string? carrotTemplate;

    private string carrotLabel = string.Empty;

    private string carrotTooltip = string.Empty;

    private int carrotTooltipMax = -1;

    private RouteMenu? routeMenu;

    private TreasureSenseRow? senseRow;

    private readonly HuntSenseCast senseCast = new(module);

    // 主視窗那一列的文字只在記錄或語言變了才重組。
    private int summaryVersion = -1;

    private string? summaryChestTemplate;

    private string summaryChests = string.Empty;

    private string summaryVisited = string.Empty;

    public override AutomationKind Kind
    {
        get => AutomationKind.TreasureHunt;
    }

    // 開過的寶箱還選得到好幾秒：不算要開的寶箱（順路與走到點都一樣），走到點時照「那裡沒有寶箱」走下一步。
    protected override IEnumerable<IGameObject> GetValidObjects()
    {
        return ValidCoffers().Where(static o => !SkipIfOpened(o));
    }

    // 只在列舉物件表的這一幀讀旗標，不保存物件。
    private static unsafe bool SkipIfOpened(IGameObject coffer)
    {
        if (!IsOpened((GameObject*)(void*)coffer.Address))
        {
            return false;
        }

        if (Player.DistanceTo(coffer) <= 5f)
        {
            NoteSkipped(coffer.GameObjectId, coffer.Position, "在 5 米內，順路不再送開箱步驟");
        }

        return true;
    }

    private static unsafe bool IsOpened(GameObject* gameObject)
    {
        return gameObject != null && ((NativeTreasure*)gameObject)->Flags.HasFlag(NativeTreasure.TreasureFlags.Opened);
    }

    // 每個寶箱物件最多寫一行；只在框架執行緒用。
    private const int SkipLogCapacity = 256;

    private static readonly HashSet<ulong> SkipLogged = [];

    private static void NoteSkipped(ulong objectId, Vector3 position, string what)
    {
        if (SkipLogged.Count >= SkipLogCapacity)
        {
            SkipLogged.Clear();
        }

        if (!SkipLogged.Add(objectId))
        {
            return;
        }

        Svc.Log.Debug(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[BOCCHI][開箱步驟] 寶箱已經開了，不再互動：位置 ({position.X:F1}, {position.Y:F1}, {position.Z:F1})；{what}"));
    }

    private static IEnumerable<IGameObject> ValidCoffers()
    {
        return Svc.Objects
            .Where(o => o is
            {
                ObjectKind: ObjectKind.Treasure,
                IsDead: false,
                IsTargetable: true,
            } && o.IsValid());
    }

    protected override Vector3 GetDestinationForCurrentStep()
    {
        return Treasure.First(t => t.Id == CurrentStep.NodeId).Position;
    }

    protected override bool TryGetLedge(uint node, out HuntLedge ledge)
    {
        return TreasureData.Ledges.TryGetValue(node, out ledge);
    }

    protected override bool TryGetNodePosition(uint node, out Vector3 position)
    {
        foreach (var datum in Treasure)
        {
            if (datum.Id == node)
            {
                position = datum.Position;
                return true;
            }
        }

        position = default;
        return false;
    }

    protected override IPathfinder? CreatePathfinder()
    {
        Treasure.Clear();

        // 版面還沒就緒就放棄這次建路（fail-closed），Hunter.Update() 下一拍會重試。
        if (!TreasureLayout.TryRead(Treasure, out var why))
        {
            Svc.Log.Warning(why);
            return null;
        }

        Treasure = Treasure.OrderBy(t => t.Id).ToList();
        module.UseSpawnPoints(Treasure);

        var chestFirst = module.Config.HuntChestFirstMaxSeconds;
        return new Pathfinder(Treasure, module.PluginConfig.PathfinderConfig.ReturnCost, module.PluginConfig.PathfinderConfig.TeleportCost,
            chestFirst >= TreasureConfig.ChestFirstAlways ? float.PositiveInfinity : chestFirst);
    }

    protected override Func<Chain> GetInteractionChain(IGameObject obj)
    {
        // 只在拿到 obj 的這一幀抄走 ID 與位置；物件包裝跨幀會被就地改寫成別的物件或懸空，每次用時重查。
        var position = obj.Position;
        return CreateOpenChain(obj.GameObjectId, position, () => module.HuntMemory.NoteOpened(position));
    }

    // 開箱數取寶箱記錄：開箱步驟沒看到就結束（人被推開、你自己開的）的寶箱也算得到，同一個寶箱只算一次。
    protected override int ChestsOpenedSoFar(TreasureType type)
    {
        return module.HuntMemory.OpenedCount(type);
    }

    /// <summary>
    /// 站到寶箱旁之後的開箱步驟（寶箱巡迴與非法模式順路開白銀寶箱共用）：每 250 毫秒互動一次，
    /// 寶箱開了、不在了或人離太遠（超過 2 米）就結束；互動前先讀已開旗標，已經開了就不再互動（沒開就照舊重試）。
    /// 時間上限沿用動作鏈預設（10 分鐘），要更短由呼叫端給。
    /// </summary>
    public static Func<Chain> CreateOpenChain(ulong objectId, Vector3 position)
    {
        return CreateOpenChain(objectId, position, null);
    }

    /// <param name="onOpened">這條步驟看到寶箱從沒開變成開了時呼叫一次（巡迴記錄用，規則見 <see cref="ChestOpenWatch"/>）；只在 framework 執行緒。</param>
    public static Func<Chain> CreateOpenChain(ulong objectId, Vector3 position, Action? onOpened)
    {
        return () =>
        {
            var watch = new ChestOpenWatch();
            return Chain.Create()
                .BreakIf(() => !ValidCoffers().Any(o => Vector3.Distance(o.Position, position) <= DISTANCE_TO_NODE_TO_USE))
                .Then(new TaskManagerTask(() =>
                {
                    if (!EzThrottler.Throttle("ChestInteract", 250))
                    {
                        return false;
                    }

                    var current = Svc.Objects.SearchById(objectId);
                    if (current == null || current.ObjectKind != ObjectKind.Treasure)
                    {
                        return true;
                    }

                    var distance = Player.DistanceTo(current);
                    InteractRangeDiag.CompareChest(current, distance, DISTANCE_TO_NODE_TO_USE);
                    if (distance > DISTANCE_TO_NODE_TO_USE)
                    {
                        return true;
                    }

                    unsafe
                    {
                        var gameObject = (GameObject*)(void*)current.Address;
                        var interacted = false;
                        if (watch.ShouldInteract(IsOpened(gameObject)))
                        {
                            Svc.Targets.Target = current;
                            OwnInteraction.Note(Environment.TickCount64);
                            TargetSystem.Instance()->InteractWithObject(gameObject);
                            if (!IsOpened(gameObject))
                            {
                                return false;
                            }

                            interacted = true;
                        }

                        if (watch.TakeOpened())
                        {
                            onOpened?.Invoke();
                        }
                        else if (!interacted)
                        {
                            NoteSkipped(objectId, position, "開箱步驟開始時就已經開了");
                        }

                        return true;
                    }
                }));
        };
    }

    protected override List<uint> GetValidNodes(int max)
    {
        return TreasureData.Levels.Where(node => node.Value <= max).Select(node => node.Key).ToList();
    }

    public override long ResumeExpiryMs
    {
        get => 0;
    }

    protected override bool HasDirection
    {
        get => true;
    }

    protected override HuntDirection ConfiguredDirection
    {
        get => module.Config.HuntDirection;
    }

    protected override bool TracksCoverage
    {
        get => true;
    }

    protected override bool IsNodeCovered(uint node)
    {
        if (module.HuntMemory.IsCovered(node) || module.HuntMemory.IsUnreachable(node))
        {
            return true;
        }

        if (!module.SenseSkips(node))
        {
            return false;
        }

        NoteSenseSkip();
        return true;
    }

    // 這一輪已經寫過的「感知數開滿」（含已記錄還沒開的）：感知世代＋開滿的種類（銀 1、銅 2）；0＝還沒寫過。
    private long senseSkipLogged;

    // 開滿的種類或感知數變了才寫一行（每一輪重算）。
    private void NoteSenseSkip()
    {
        var sense = module.SenseCount;
        var mask = (module.SenseExhausted(TreasureType.Silver) ? 1 : 0) | (module.SenseExhausted(TreasureType.Bronze) ? 2 : 0);
        var key = ((long)sense.Generation << 2) | (uint)mask;
        if (key == senseSkipLogged)
        {
            return;
        }

        senseSkipLogged = key;
        var skipped = 0;
        for (var i = Math.Max(stepIndex, 0); i < Steps.Count; i++)
        {
            if (Steps[i].Type == PathfinderStepType.WalkToNode && module.SenseSkips(Steps[i].NodeId))
            {
                skipped++;
            }
        }

        var kinds = mask switch
        {
            1 => "白銀寶箱",
            2 => "青銅寶箱",
            _ => "寶箱",
        };
        Svc.Log.Information($"{SenseTag} 魔尋寶感知數開滿（{module.SenseProgressText()}，感知後 {(Environment.TickCount64 - sense.SensedAt) / 1000} 秒）："
                            + $"剩下的路線跳過 {skipped} 個{kinds}生成點（有已記錄寶箱的點照樣去開）");
    }

    private const string SenseTag = "[BOCCHI][寶箱巡迴]";

    protected override void OnRunStarted(bool resumed)
    {
        senseCast.Arm();
        senseSkipLogged = 0;
        attempted.Clear();
        if (!resumed)
        {
            CaptureRoutes();
            Carrots.Clear();
        }
    }

    protected override bool TryPickCarrot(out ulong objectId, out Vector3 position)
    {
        return Carrots.TryPick(module.Config, stepIndex + 1, CurrentStep.Type, out objectId, out position);
    }

    protected override CarrotStepAction OnCarrotStep(PathfinderStep step)
    {
        return Carrots.Step(step, StepProcessor, vnav);
    }

    protected override void OnCarrotUnreachable(PathfinderStep step, string what)
    {
        Carrots.Unreachable(what);
    }

    protected override void OnRunStopped()
    {
        senseCast.Disarm();
        Carrots.Stop();
    }

    protected override bool HoldBeforePlan()
    {
        return senseCast.Hold();
    }

    protected override bool HasRoundFilter
    {
        get => roundExcluded.Count > 0;
    }

    protected override bool IsInRound(uint node)
    {
        return TreasureRoutes.Nodes.TryGetValue(node, out var route) && !roundExcluded.Contains(route);
    }

    protected override string RoundRouteLabel
    {
        get => roundExcluded.Count == 0 ? "全部" : RouteKeys(RoundSelection);
    }

    protected override string RoundRangeSuffix(Module<Plugin, Config> owner)
    {
        return roundExcluded.Count == 0
            ? string.Empty
            : module.T("panel.hunt.routes.range_suffix")
                .Replace("{count}", (TreasureRoutes.Order.Length - roundExcluded.Count).ToString())
                .Replace("{total}", TreasureRoutes.Order.Length.ToString());
    }

    // 不認得的值略過；設定裡每一段都不巡（只可能是手改設定檔）就當全部巡。
    private void CaptureRoutes()
    {
        var config = module.Config;
        roundExcluded.Clear();
        foreach (var route in config.HuntRoutesExcluded)
        {
            if (TreasureRoutes.IndexOf(route) >= 0)
            {
                roundExcluded.Add(route);
            }
        }

        if (roundExcluded.Count >= TreasureRoutes.Order.Length)
        {
            roundExcluded.Clear();
        }

        roundChestsInRoutesOnly = config.HuntChestsInRoutesOnly;
    }

    // 所選路線的位元（同 TreasureRouteBoard.SelectionOf）。
    private int RoundSelection
    {
        get
        {
            var selection = 0;
            for (var i = 0; i < TreasureRoutes.Order.Length; i++)
            {
                if (!roundExcluded.Contains(TreasureRoutes.Order[i]))
                {
                    selection |= 1 << i;
                }
            }

            return selection;
        }
    }

    // 記錄用：列舉名稱、「+」分隔（不跟語言變）。
    private static string RouteKeys(int selection)
    {
        return string.Join("+", TreasureRoutes.Order.Where(route => TreasureRouteBoard.IsSelected(selection, route)));
    }

    // 給人看：台服名稱、「、」分隔；全部選就是「全部」。
    private string RouteNames(int selection)
    {
        if (selection == TreasureRouteBoard.All)
        {
            return module.T("panel.hunt.routes.all");
        }

        var board = Menu.Board;
        var names = new List<string>();
        for (var i = 0; i < TreasureRoutes.Order.Length; i++)
        {
            if (TreasureRouteBoard.IsSelected(selection, TreasureRoutes.Order[i]))
            {
                names.Add(board.Name(i));
            }
        }

        return string.Join("、", names);
    }

    private static readonly HuntDirection[] Directions = [HuntDirection.Forward, HuntDirection.Reverse, HuntDirection.Alternate];
}
