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
public class TreasureHunt(TreasureModule module) : Hunter(module)
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
                    if (current == null || current.ObjectKind != ObjectKind.Treasure || Player.DistanceTo(current) > DISTANCE_TO_NODE_TO_USE)
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

    protected override void AdjustPlan(List<uint> inRange, List<uint> planned, List<uint> priority)
    {
        var memory = module.HuntMemory;
        var chestsInRoutesOnly = roundChestsInRoutesOnly && HasRoundFilter;
        planRange.Clear();
        foreach (var node in inRange)
        {
            if (!chestsInRoutesOnly || IsInRound(node))
            {
                planRange.Add(node);
            }
        }

        var skipped = planned.RemoveAll(memory.IsCovered);
        planned.RemoveAll(memory.IsUnreachable);
        var senseSkipped = planned.RemoveAll(module.SenseSkips);
        if (senseSkipped > 0)
        {
            Svc.Log.Information($"{SenseTag} 魔尋寶感知數開滿（{module.SenseProgressText()}）：規劃時跳過 {senseSkipped} 個生成點（有已記錄寶箱的點照樣排）");
        }

        var added = 0;
        var outside = 0;
        var avoided = 0;
        var unreachable = 0;
        var otherRoute = 0;
        var fromOtherRoutes = 0;
        var chests = memory.Chests;
        for (var i = 0; i < chests.Count; i++)
        {
            var node = chests[i].Node;
            if (TreasureData.IsAvoided(node))
            {
                avoided++;
                continue;
            }

            if (node == 0 || !planRange.Contains(node))
            {
                if (node != 0 && inRange.Contains(node))
                {
                    otherRoute++;
                }
                else
                {
                    outside++;
                }

                continue;
            }

            if (memory.IsUnreachable(node))
            {
                unreachable++;
                continue;
            }

            if (!planned.Contains(node))
            {
                planned.Add(node);
                added++;
                if (HasRoundFilter && !IsInRound(node))
                {
                    fromOtherRoutes++;
                }
            }

            if (!priority.Contains(node))
            {
                priority.Add(node);
            }
        }

        if (HasRoundFilter)
        {
            Svc.Log.Information($"[BOCCHI][寶箱巡迴] 這一輪只巡路線「{RouteNames(RoundSelection)}」（{TreasureRoutes.Order.Length - roundExcluded.Count}/{TreasureRoutes.Order.Length} 段）："
                                + $"區域等級上限內 {inRange.Count(IsInRound)} 個點；其他路線的已記錄寶箱 "
                                + (chestsInRoutesOnly ? $"{otherRoute} 個不去開（只開所選路線內的）" : $"{fromOtherRoutes} 個照樣去開"));
        }

        if (skipped == 0 && priority.Count == 0 && outside == 0 && avoided == 0 && unreachable == 0)
        {
            return;
        }

        Svc.Log.Information($"[BOCCHI][寶箱記錄] 規劃寶箱巡迴：略過巡過的生成點 {skipped} 個；已記錄的寶箱 {priority.Count} 個一起排進這一輪"
                            + (added > 0 ? $"（其中 {added} 個原本不在要跑的點裡）" : string.Empty)
                            + (outside > 0 ? $"；另有 {outside} 個在區域等級上限外或對不上生成點，不去" : string.Empty)
                            + (avoided > 0 ? $"；{avoided} 個在刻意不去的生成點，只記錄不去開" : string.Empty)
                            + (unreachable > 0 ? $"；{unreachable} 個之前前往失敗，跳過到按清除或換區" : string.Empty)
                            + $"；這一輪共 {planned.Count} 個點");
    }

    protected override bool TryPickDetour(out uint node, out bool append)
    {
        node = 0;
        append = false;
        var memory = module.HuntMemory;
        if (memory.ChestCount == 0)
        {
            return false;
        }

        var player = Player.Position;
        var detour = TreasureDetourRules.Pick(Steps, stepIndex, player, GetDetectionRange(), memory, planRange, attempted);
        if (detour.Kind == TreasureDetourKind.None)
        {
            return false;
        }

        var chest = detour.Chest;
        node = chest.Node;
        append = detour.Kind == TreasureDetourKind.Append;
        attempted.Add(node);
        var position = chest.Position;
        var where = append ? "不在剩下的路線上，排到最後" : $"插在第 {stepIndex + 1} 步（{CurrentStep.Type}）前面，開完接回原本路線";
        Svc.Log.Information(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[BOCCHI][寶箱記錄] 去開已記錄的寶箱：{TypeLabel(chest.Type)} 生成點 {node} 位置 ({position.X:F1}, {position.Y:F1}, {position.Z:F1}) 距離 {detour.Distance:F1} 米；{where}"));
        return true;
    }

    protected override void OnNodeAbsent(uint node)
    {
        module.HuntMemory.NoteAbsent(node, Environment.TickCount64);
    }

    // 只有已記錄寶箱的點才跳過：沒有寶箱的點走不到照舊停止巡迴（多半是 vnavmesh 整個不能用，逐點跳過只會一路空轉）。
    protected override bool TrySkipUnreachableNode(uint node)
    {
        var memory = module.HuntMemory;
        if (!memory.HasChestAt(node))
        {
            return false;
        }

        memory.NoteUnreachable(node);
        attempted.Add(node);
        var chests = memory.Chests;
        for (var i = 0; i < chests.Count; i++)
        {
            if (chests[i].Node != node)
            {
                continue;
            }

            var position = chests[i].Position;
            Svc.Log.Information(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"[BOCCHI][寶箱記錄] 前往已記錄的寶箱失敗：{TypeLabel(chests[i].Type)} 生成點 {node} 位置 ({position.X:F1}, {position.Y:F1}, {position.Z:F1})；這個寶箱先跳過（照樣記錄），到按清除或換區前不再去開，巡迴繼續下一步"));
            break;
        }

        return true;
    }

    protected override void OnNodeStepDone(uint node)
    {
        attempted.Add(node);
    }

    protected override bool HasExtraRows
    {
        get => true;
    }

    protected override void DrawExtraRows(Module<Plugin, Config> owner)
    {
        var memory = module.HuntMemory;
        RefreshSummary(memory);

        ImGui.Indent(16);
        (senseRow ??= new TreasureSenseRow()).Draw(module, UnknownColor);
        MainPanelUi.Labelled(module.T("panel.hunt.memory.chests"), summaryChests);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(module.T("panel.hunt.memory.tooltip"));
        }

        MainPanelUi.Gap();
        if (memory.HasSpawnPoints)
        {
            MainPanelUi.Labelled(module.T("panel.hunt.memory.visited"), summaryVisited);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(memory.Participating ? module.T("panel.hunt.memory.participating_tooltip") : module.T("panel.hunt.memory.tooltip"));
            }
        }
        else
        {
            // 生成點還沒讀到時不記巡過：畫成 ? 而不是 0。
            ImGui.TextColored(OcelotColor.Yellow, $"{module.T("panel.hunt.memory.visited")}:");
            ImGui.SameLine();
            ImGui.TextColored(UnknownColor, "?");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(module.T("panel.hunt.memory.unknown_tooltip"));
            }
        }

        ImGui.SameLine();
        var any = memory.ChestCount > 0 || memory.VisitedCount > 0;
        if (ImGuiEx.IconButton(ClearIcon, "treasure-hunt-memory-clear", enabled: any))
        {
            module.ClearHuntMemory(I18N.T("automation.source.main_window"));
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(module.T("panel.hunt.memory.clear_tooltip"));
        }

        DrawDirection();
        DrawRoutes();
        DrawCarrotToggle();
        ImGui.Unindent(16);
    }

    // 同設定視窗「順路挖幸運胡蘿蔔」；改了不必重新開始，之後看到的胡蘿蔔就照新的設定。
    private void DrawCarrotToggle()
    {
        var config = module.Config;
        var template = module.T("panel.hunt.carrots.label");
        if (!ReferenceEquals(template, carrotTemplate) || carrotTooltipMax != config.HuntCarrotMaxDistance)
        {
            carrotTemplate = template;
            carrotTooltipMax = config.HuntCarrotMaxDistance;
            carrotLabel = template + "##treasure-hunt-carrots";
            carrotTooltip = module.T("panel.hunt.carrots.tooltip").Replace("{max}", carrotTooltipMax.ToString());
        }

        var on = config.HuntDigCarrots;
        if (ImGui.Checkbox(carrotLabel, ref on))
        {
            config.HuntDigCarrots = on;
            module.PluginConfig.Save();
            Svc.Log.Information($"[BOCCHI][順路胡蘿蔔] 「順路挖幸運胡蘿蔔」改成「{(on ? "開" : "關")}」（主視窗）：距離上限 {config.HuntCarrotMaxDistance} 米；之後看到的胡蘿蔔照新的設定（已經插隊的那一根照樣做完）");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(carrotTooltip);
        }
    }

    /// <summary>由模組每幀呼叫（顯示寶箱巡迴、在島上時）；實際每秒最多重算一次。</summary>
    public void UpdateRouteBoard()
    {
        Menu.Board.Update(module.HuntMemory, module.PluginConfig.PathfinderConfig, TreasureRouteBoard.SelectionOf(module.Config.HuntRoutesExcluded), Environment.TickCount64);
    }

    private CarrotDetour Carrots
    {
        get => carrotDetour ??= new CarrotDetour();
    }

    private RouteMenu Menu
    {
        get => routeMenu ??= new RouteMenu();
    }

    // 「路線」選單的板子與文字快取（第一次用到才建）；文字只在板子、選擇、設定或語言變了才重組。
    private sealed class RouteMenu
    {
        public readonly TreasureRouteBoard Board = new();

        public readonly string[] Labels = new string[TreasureRoutes.Order.Length];

        public readonly string[] Values = new string[TreasureRoutes.Order.Length];

        public readonly string[] Tips = new string[TreasureRoutes.Order.Length];

        public string Preview = string.Empty;

        public string PreviewTip = string.Empty;

        public string AllLabel = string.Empty;

        public string AllTip = string.Empty;

        public string OnlyLabel = string.Empty;

        public string OnlyTip = string.Empty;

        public string Other = string.Empty;

        public string OtherTip = string.Empty;

        public int Version = -1;

        public int Selection = -1;

        public bool Only;

        public string? Template;
    }

    // 開始前就選得到；改了從下一次按開始生效，這一輪（含暫停後續跑）照開始時選的路線。至少要留一段。
    private void DrawRoutes()
    {
        var config = module.Config;
        var menu = Menu;
        var selection = TreasureRouteBoard.SelectionOf(config.HuntRoutesExcluded);
        RefreshRouteTexts(menu, selection, config.HuntChestsInRoutesOnly);

        MainPanelUi.Gap();
        ImGui.TextColored(OcelotColor.Yellow, $"{module.T("panel.hunt.routes.label")}:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
        if (ImGui.BeginCombo("##treasure-hunt-routes", menu.Preview, ImGuiComboFlags.HeightLarge))
        {
            DrawRouteMenu(menu, config, selection);
            ImGui.EndCombo();
        }
        else if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(menu.PreviewTip);
        }

        if (selection != TreasureRouteBoard.All && menu.Board.OtherRouteChests > 0)
        {
            ImGui.TextUnformatted(menu.Other);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(menu.OtherTip);
            }
        }
    }

    private void DrawRouteMenu(RouteMenu menu, TreasureConfig config, int selection)
    {
        var all = selection == TreasureRouteBoard.All;
        var allOn = all;
        if (ImGui.Checkbox(menu.AllLabel, ref allOn) && allOn && !all)
        {
            config.HuntRoutesExcluded.Clear();
            RoutesChanged(menu, TreasureRouteBoard.All);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(menu.AllTip);
        }

        ImGui.Separator();
        if (ImGui.BeginTable("##treasure-hunt-route-table", 2, ImGuiTableFlags.SizingFixedFit))
        {
            var count = BitOperations.PopCount((uint)selection);
            for (var i = 0; i < TreasureRoutes.Order.Length; i++)
            {
                var route = TreasureRoutes.Order[i];
                var on = TreasureRouteBoard.IsSelected(selection, route);
                var next = on;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Checkbox(menu.Labels[i], ref next) && next != on && (next || count > 1))
                {
                    if (next)
                    {
                        config.HuntRoutesExcluded.RemoveAll(excluded => excluded == route);
                    }
                    else
                    {
                        config.HuntRoutesExcluded.Add(route);
                    }

                    RoutesChanged(menu, next ? selection | (1 << i) : selection & ~(1 << i));
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(menu.Tips[i]);
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(menu.Values[i]);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(menu.Tips[i]);
                }
            }

            ImGui.EndTable();
        }

        ImGui.Separator();
        var only = config.HuntChestsInRoutesOnly;
        if (ImGui.Checkbox(menu.OnlyLabel, ref only))
        {
            config.HuntChestsInRoutesOnly = only;
            module.PluginConfig.Save();
            menu.Board.RefreshSoon();
            var current = IsRunning || Progress.HasSaved ? $"；這一輪（含續跑）照原本的「{(roundChestsInRoutesOnly ? "開" : "關")}」" : string.Empty;
            Svc.Log.Information($"[BOCCHI][寶箱巡迴] 「只開所選路線內的已記錄寶箱」改成「{(only ? "開" : "關")}」（主視窗）：下一次按開始生效{current}");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(menu.OnlyTip);
        }
    }

    private void RoutesChanged(RouteMenu menu, int selection)
    {
        module.PluginConfig.Save();
        menu.Board.RefreshSoon();
        var current = IsRunning || Progress.HasSaved ? $"；這一輪（含續跑）照原本的「{RouteNames(RoundSelection)}」" : string.Empty;
        Svc.Log.Information($"[BOCCHI][寶箱巡迴] 路線改成「{RouteNames(selection)}」（主視窗）：下一次按開始生效{current}");
    }

    private void RefreshRouteTexts(RouteMenu menu, int selection, bool only)
    {
        var template = module.T("panel.hunt.routes.value");
        var board = menu.Board;
        if (menu.Version == board.Version && menu.Selection == selection && menu.Only == only && ReferenceEquals(template, menu.Template))
        {
            return;
        }

        menu.Version = board.Version;
        menu.Selection = selection;
        menu.Only = only;
        menu.Template = template;
        var count = BitOperations.PopCount((uint)selection);
        var maxLevel = board.MaxLevel > 0 ? board.MaxLevel : PathfinderConfig.LevelCeiling;
        var tipTemplate = module.T("panel.hunt.routes.route_tooltip");
        for (var i = 0; i < TreasureRoutes.Order.Length; i++)
        {
            var row = board.Rows[i];
            var name = board.Name(i);
            menu.Labels[i] = $"{name}##treasure-hunt-route-{i}";
            menu.Values[i] = template
                .Replace("{count}", Known(row.Count))
                .Replace("{left}", Known(row.Remaining))
                .Replace("{time}", Clock(row.RoundSeconds));
            var tip = tipTemplate
                .Replace("{name}", name)
                .Replace("{count}", Known(row.Count))
                .Replace("{max}", board.MaxLevel > 0 ? board.MaxLevel.ToString() : "?")
                .Replace("{left}", Known(row.Remaining))
                .Replace("{left_time}", Clock(row.RemainingSeconds))
                .Replace("{time}", Clock(row.RoundSeconds))
                .Replace("{speed}", TreasureRouteStats.MetersPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("{nodes}", string.Join("、", TreasureRoutes.NodesOf(TreasureRoutes.Order[i], maxLevel)));
            menu.Tips[i] = count == 1 && TreasureRouteBoard.IsSelected(selection, TreasureRoutes.Order[i]) ? tip + "\n" + module.T("panel.hunt.routes.last_tooltip") : tip;
        }

        var allName = module.T("panel.hunt.routes.all");
        menu.Preview = selection == TreasureRouteBoard.All ? allName : $"{count}/{TreasureRoutes.Order.Length}：{RouteNames(selection)}";
        menu.PreviewTip = module.T("panel.hunt.routes.tooltip").Replace("{routes}", RouteNames(selection));
        menu.AllLabel = $"{allName}##treasure-hunt-route-all";
        menu.AllTip = module.T("panel.hunt.routes.all_tooltip");
        menu.OnlyLabel = $"{module.T("panel.hunt.routes.chests_only")}##treasure-hunt-route-chests";
        menu.OnlyTip = module.T("panel.hunt.routes.chests_only_tooltip");
        menu.Other = module.T("panel.hunt.routes.other_chests").Replace("{count}", board.OtherRouteChests.ToString());
        menu.OtherTip = module.T(only ? "panel.hunt.routes.other_chests_skip_tooltip" : "panel.hunt.routes.other_chests_open_tooltip");
    }

    private static string Known(int value)
    {
        return value < 0 ? "?" : value.ToString();
    }

    private static string Clock(float seconds)
    {
        if (seconds < 0)
        {
            return "?";
        }

        var total = (int)MathF.Round(seconds);
        return $"{total / 60}:{total % 60:00}";
    }

    private static readonly HuntDirection[] Directions = [HuntDirection.Forward, HuntDirection.Reverse, HuntDirection.Alternate];

    // 開始前就選得到；改了從下一次按開始生效，這一輪（含暫停後續跑）照開始時的方向。
    private void DrawDirection()
    {
        var config = module.Config;
        ImGui.TextColored(OcelotColor.Yellow, $"{I18N.T("hunter.direction.label")}:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 7);
        if (ImGui.BeginCombo("##treasure-hunt-direction", DirectionLabel(config.HuntDirection)))
        {
            foreach (var option in Directions)
            {
                if (ImGui.Selectable(DirectionLabel(option), option == config.HuntDirection) && option != config.HuntDirection)
                {
                    config.HuntDirection = option;
                    module.PluginConfig.Save();
                    var current = IsRunning || Progress.HasSaved ? $"；這一輪（含續跑）照原本的{(RoundReversed ? "逆向" : "正向")}" : string.Empty;
                    Svc.Log.Information($"[BOCCHI][寶箱巡迴] 方向改成「{DirectionLabel(option)}」（主視窗）：下一次按開始生效{current}");
                }
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(I18N.T("hunter.direction.tooltip"));
        }
    }

    private static string DirectionLabel(HuntDirection direction)
    {
        return I18N.T(direction switch
        {
            HuntDirection.Reverse => "hunter.direction.reverse",
            HuntDirection.Alternate => "hunter.direction.alternate",
            _ => "hunter.direction.forward",
        });
    }

    private void RefreshSummary(TreasureHuntMemory memory)
    {
        var template = module.T("panel.hunt.memory.chests_value");
        if (summaryVersion == memory.Version && ReferenceEquals(template, summaryChestTemplate))
        {
            return;
        }

        summaryVersion = memory.Version;
        summaryChestTemplate = template;
        summaryChests = template
            .Replace("{total}", memory.ChestCount.ToString())
            .Replace("{bronze}", memory.CountChests(TreasureType.Bronze).ToString())
            .Replace("{silver}", memory.CountChests(TreasureType.Silver).ToString());
        summaryVisited = module.T("panel.hunt.memory.visited_value").Replace("{count}", memory.VisitedCount.ToString());
    }

    private string TypeLabel(TreasureType type)
    {
        return module.T(type == TreasureType.Silver ? "panel.coffer.silver" : "panel.coffer.bronze");
    }
}
