using System;
using System.Linq;
using System.Numerics;
using BOCCHI.Automation;
using BOCCHI.Chains;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Ledger;
using BOCCHI.Modules.Automator;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.Fates;
using BOCCHI.Modules.StateManager;
using Dalamud.Interface;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;
using Ocelot.IPC;
using Ocelot;
using System.Collections.Generic;

namespace BOCCHI.Modules.Teleporter;

// 本檔契約：「打完自動返回」在魔法甕之後先等 EurekaHelper 尋寶（PotReturnWait）；問不到端點時與沒有這段時完全相同。
// 狀態、IPC、F.A.T.E. 表全部只在框架執行緒讀寫（狀態機事件、Framework.Update、自動化控制）。
public class Teleporter(TeleporterModule module)
{
    private const long PotWaitPollMs = 250;

    private readonly PotReturnWait potWait = new();

    private readonly EurekaPotHuntIpc potHuntIpc = new("自動返回照常");

    private long nextPotWaitPollAt;

    // 這一輪是哪個魔法甕在等（0＝別的活動結束時對方已在尋寶）；等完要補送哪一種返回。
    private uint potWaitFateId;

    private string potWaitTrigger = string.Empty;

    private bool pendingAfterFate;

    private bool pendingAfterCriticalEncounter;

    // 進入 F.A.T.E. 範圍那一刻讀到的 id 與名稱：離開時 CurrentFate 已經是 null，只能靠這份分辨是不是魔法甕。
    private uint enteredFateId;

    private string enteredFateName = string.Empty;

    private readonly FateEndConfirm fateEnd = new();

    // 使用者在這一場進行中手動停止非法模式（他接手了）：這一場結束時不自動返回，結束時清掉。
    private uint takeoverFateId;

    private bool takeoverCriticalEncounter;

    private FatesModule? fates;

    private bool fatesResolved;

    /// <summary>「打完自動返回」有沒有被使用者按下開始；只存在記憶體裡，載入時一律是 false。</summary>
    public bool ReturnArmed { get; private set; }

    public void ArmReturn()
    {
        ReturnArmed = true;
    }

    public void DisarmReturn()
    {
        ReturnArmed = false;
        fateEnd.Cancel();
        CancelPotWait("打完自動返回已停止");
    }

    /// <summary>使用者手動停止非法模式時呼叫（自動停止不呼叫）：正在進行的那一場結束時不自動返回。</summary>
    public void NoteIllegalModeStoppedByUser()
    {
        var state = module.GetModule<StateManagerModule>().GetState();
        takeoverFateId = state == State.InFate ? enteredFateId : 0;
        takeoverCriticalEncounter = state == State.InCriticalEncounter;
        if (!ReturnArmed || (takeoverFateId == 0 && !takeoverCriticalEncounter))
        {
            return;
        }

        var label = EventLabel(takeoverFateId != 0, takeoverFateId, enteredFateName);
        Svc.Log.Information($"[BOCCHI][打完自動返回] 你在{label}進行中停止了非法模式：這一場結束時不自動返回（你接手了），下一場照常。");
    }

    /// <returns>有沒有畫出按鈕（vnavmesh 沒就緒時一個都不畫）。</returns>
    public bool Button(Aethernet? aethernet, Vector3 destination, string name, string id, EventData ev)
    {
        if (!module.TryGetIPCSubscriber<VNavmesh>(out var vnav) || vnav == null || !vnav.IsReady())
        {
            return false;
        }

        if (aethernet == null)
        {
            aethernet = ZoneData.GetClosestAethernetShard(destination);
        }

        // 兩顆鈕共用同一個 ID 字串：每一列每幀只組一次。
        var buttonId = $"{name}##{id}";
        ImGui.Indent(16);
        PathfindingButton(destination, name, buttonId, ev);
        TeleportButton((Aethernet)aethernet, destination, buttonId, ev);
        ImGui.Unindent(16);
        return true;
    }

    // 圖示字元先轉好：IconButton(FontAwesomeIcon) 每次呼叫都會新建一個字串。
    private static readonly string RunningIcon = FontAwesomeIcon.Running.ToIconString();

    private static readonly string LocationArrowIcon = FontAwesomeIcon.LocationArrow.ToIconString();

    private void PathfindingButton(Vector3 destination, string name, string buttonId, EventData ev)
    {
        if (!module.TryGetIPCSubscriber<VNavmesh>(out var vnav) || vnav == null || !vnav.IsReady())
        {
            return;
        }

        if (ImGuiEx.IconButton(RunningIcon, buttonId))
        {
            SubmitPathfinding(vnav, destination, name, ev);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(I18N.T("modules.teleporter.tooltip.pathfind_to", new Dictionary<string, string> { ["name"] = name }));
        }

        if (!module.TryGetIPCSubscriber<Lifestream>(out var lifestream) || lifestream == null || !lifestream.IsReady())
        {
            return;
        }

        ImGui.SameLine();
    }

    /// <summary>主視窗「跑過去」那條尋路動作鏈（跟團刷 CE 也用）；vnavmesh 沒就緒時不送、回 false。只在框架執行緒呼叫。</summary>
    public bool PathfindTo(Vector3 destination, string name, EventData ev)
    {
        if (!module.TryGetIPCSubscriber<VNavmesh>(out var vnav) || vnav == null || !vnav.IsReady())
        {
            return false;
        }

        SubmitPathfinding(vnav, destination, name, ev, "跟團刷CE");
        return true;
    }

    private void SubmitPathfinding(VNavmesh vnav, Vector3 destination, string name, EventData ev, string origin = "跑過去")
    {
        Svc.Log.Info($"Pathfinding to {name} at {destination}");

        Plugin.Chain.Submit(() => WithStandSpot(Chain.Create("Pathfinding")
                .Then(new PathfindingChain(vnav, destination, ev, TravelSource.TeleporterWalk, 20f)), vnav, ev, origin)
            .ConditionalThen(_ => module.Config.ShouldMount, ChainHelper.MountChain())
            .WaitUntilNear(vnav, destination, 205f)
        );
    }

    // 緊急遭遇戰、設定開著才在送出走路後接一步 CeStandSpot.Follow（另開動作鏈挑報名站位）；F.A.T.E. 與其他目的地原樣回傳。
    private Chain WithStandSpot(Chain chain, VNavmesh vnav, EventData ev, string origin)
    {
        if (ev.Type != EventType.CriticalEncounter
            || !module.PluginConfig.CriticalEncountersConfig.RandomRegistrationSpot
            || !module.TryGetModule<CriticalEncountersModule>(out var encounters)
            || encounters == null)
        {
            return chain;
        }

        return chain.Then(_ => CeStandSpot.Follow(vnav, encounters, ev.Id, origin));
    }

    private void TeleportButton(Aethernet aethernet, Vector3 destination, string buttonId, EventData ev)
    {
        if (!module.TryGetIPCSubscriber<Lifestream>(out var lifestream) || lifestream == null || !lifestream.IsReady())
        {
            return;
        }

        var isNearShards = ZoneData.IsNearAnyAethernetShard();
        var isNearCurrentShard = ZoneData.IsNearAethernetShard(aethernet);

        if (ImGuiEx.IconButton(LocationArrowIcon, buttonId, enabled: isNearShards && !isNearCurrentShard))
        {
            SubmitTeleport(aethernet, destination, ev, lifestream);
        }

        if (!ImGui.IsItemHovered())
        {
            return;
        }

        if (!isNearShards)
        {
            ImGui.SetTooltip(I18N.T("modules.teleporter.tooltip.must_be_near_aetheryte"));
        }
        else if (isNearCurrentShard)
        {
            ImGui.SetTooltip(I18N.T("modules.teleporter.tooltip.already_at_aetheryte"));
        }
        else
        {
            ImGui.SetTooltip(I18N.T("modules.teleporter.tooltip.teleport_to", new Dictionary<string, string> { ["name"] = aethernet.ToFriendlyString() }));
        }
    }

    // 刻意獨立成一個方法：寫在 TeleportButton 裡的話，捕捉參數的閉包在每次畫按鈕時就建立（不論有沒有按下）。
    private void SubmitTeleport(Aethernet aethernet, Vector3 destination, EventData ev, Lifestream lifestream)
    {
        Chain Factory()
        {
            var chain = Chain.Create("Teleport Sequence")
                .Then(ChainHelper.TeleportChain(aethernet))
                .Debug("Waiting for lifestream to not be 'busy'")
                .Then(new TaskManagerTask(() => !lifestream.IsBusy(), new TaskManagerConfiguration { TimeLimitMS = 30000 }));

            if (module.TryGetIPCSubscriber<VNavmesh>(out var vnav) && vnav != null && vnav.IsReady())
            {
                WithStandSpot(chain.RunIf(() => module.Config.PathToDestination)
                        .Then(new PathfindingChain(vnav, destination, ev, TravelSource.TeleporterAfterTeleport, 20f)), vnav, ev, "傳送後走過去")
                    .ConditionalThen(_ => module.Config.ShouldMount, ChainHelper.MountChain())
                    .WaitUntilNear(vnav, destination, 20f);
            }

            return chain;
        }

        Plugin.Chain.Submit(Factory);
    }

    public void OnFateEnter(StateManagerModule states)
    {
        enteredFateId = ReadCurrentFateId();
        enteredFateName = enteredFateId != 0 && TryGetFates() is { } tracked && tracked.fates.TryGetValue(enteredFateId, out var fate) ? fate.Name : string.Empty;
        if (fateEnd.IsPending)
        {
            Svc.Log.Debug($"[BOCCHI][打完自動返回] 確認離開的 F.A.T.E. 有沒有結束時又進入 F.A.T.E. 範圍（#{enteredFateId}）：不送返回。");
            fateEnd.Cancel();
        }
    }

    public void OnFateEnd(StateManagerModule states)
    {
        var fateId = enteredFateId;
        var fateName = enteredFateName;
        enteredFateId = 0;
        enteredFateName = string.Empty;
        var takenOver = fateId != 0 && fateId == takeoverFateId;
        takeoverFateId = 0;

        if (FateReturnBlocked())
        {
            return;
        }

        if (takenOver)
        {
            Svc.Log.Information($"[BOCCHI][打完自動返回] 離開{EventLabel(true, fateId, fateName)}：這一場是你中途停止非法模式後接手的，不自動返回。");
            return;
        }

        // 離開範圍那一幀它多半還在清單上：等它離開清單（打完）才返回，確認窗過了還在＝只是走出範圍。
        if (fateId != 0 && IsFateListed(fateId))
        {
            fateEnd.Begin(fateId, fateName, Environment.TickCount64);
            return;
        }

        RequestReturn(true, fateId, fateName);
    }

    /// <summary>每幀呼叫（框架執行緒）；沒有在確認時什麼都不做。</summary>
    public void TickFateEndConfirm()
    {
        if (!fateEnd.IsPending)
        {
            return;
        }

        var verdict = fateEnd.Step(Environment.TickCount64, IsFateListed(fateEnd.FateId), out var id, out var name, out var heldMs);
        if (verdict == FateEndVerdict.WalkedOut)
        {
            Svc.Log.Information($"[BOCCHI][打完自動返回] 離開{EventLabel(true, id, name)}的範圍 {LedgerRules.Seconds(heldMs)} 秒後它仍在進行（只是走出範圍，不是打完）：不送返回。");
            return;
        }

        if (verdict == FateEndVerdict.Ended && !FateReturnBlocked())
        {
            RequestReturn(true, id, name);
        }
    }

    // 打完 F.A.T.E. 後返回的三個前提；任一不成立就不送（不寫記錄）。
    private bool FateReturnBlocked()
    {
        return !ReturnArmed || module.GetModule<AutomatorModule>().IsEnabled || !module.Config.ReturnAfterFate;
    }

    public void OnCriticalEncounterEnd(StateManagerModule states)
    {
        var takenOver = takeoverCriticalEncounter;
        takeoverCriticalEncounter = false;
        if (!ReturnArmed)
        {
            return;
        }

        if (module.GetModule<AutomatorModule>().IsEnabled)
        {
            return;
        }

        if (!module.Config.ReturnAfterCriticalEncounter)
        {
            return;
        }

        if (takenOver)
        {
            Svc.Log.Information("[BOCCHI][打完自動返回] 緊急遭遇戰結束：這一場是你中途停止非法模式後接手的，不自動返回。");
            return;
        }

        RequestReturn(false, 0, string.Empty);
    }

    // 只有「已經在等」或「問得到 EurekaHelper」時才不當場返回（PotReturnWait.Decide）。
    private void RequestReturn(bool afterFate, uint fateId, string fateName)
    {
        if (MovingAutomation() is { } moving)
        {
            Svc.Log.Information($"[BOCCHI][打完自動返回] {EventLabel(afterFate, fateId, fateName)}結束時「{moving}」正在進行：不送返回，交給它自己的路線（免得兩個自動化搶著移動角色）。");
            return;
        }

        var isPot = afterFate && PotCycle.IsPot(fateId);
        var pending = potWait.IsPending;
        var active = false;
        var available = !pending && potHuntIpc.TryRead(out active);
        var start = PotReturnWait.Decide(pending, isPot, available, active);
        if (start == PotReturnStart.ReturnNow)
        {
            Return();
            return;
        }

        var label = EventLabel(afterFate, fateId, fateName);
        if (start == PotReturnStart.Absorb)
        {
            pendingAfterFate |= afterFate;
            pendingAfterCriticalEncounter |= !afterFate;
            Svc.Log.Information($"[BOCCHI][魔法甕尋寶] 打完自動返回：{label}結束時還在等 EurekaHelper 的魔法甕尋寶：這次的返回等這一輪等完再送。");
            return;
        }

        var now = Environment.TickCount64;
        pendingAfterFate = afterFate;
        pendingAfterCriticalEncounter = !afterFate;
        potWaitTrigger = label;
        nextPotWaitPollAt = now + PotWaitPollMs;
        if (isPot)
        {
            potWaitFateId = fateId;
            potWait.BeginAfterPot(now);
            Svc.Log.Information($"[BOCCHI][魔法甕尋寶] 打完自動返回：離開{label}，先不送返回：等 EurekaHelper 開始尋寶（最多 {PotHuntYield.StartGraceMs / 1000} 秒）、"
                                + $"尋寶中不送（最多 {PotHuntYield.HuntCapMs / 1000} 秒）。");
        }
        else
        {
            potWaitFateId = 0;
            potWait.BeginWhileHunting();
        }

        Advance(now, true, active);
    }

    /// <summary>每幀呼叫（框架執行緒）；沒有在等時什麼都不做。</summary>
    public void TickPotWait()
    {
        if (!potWait.IsPending)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now < nextPotWaitPollAt)
        {
            return;
        }

        nextPotWaitPollAt = now + PotWaitPollMs;
        if (module.GetModule<AutomatorModule>().IsEnabled)
        {
            CancelPotWait("非法模式開始了，尋寶改由非法模式等");
            return;
        }

        if (potWait.IsSettlingChest)
        {
            if (potHuntIpc.TryRead(out var huntActive) && huntActive)
            {
                potWait.ResumeHunting();
                Svc.Log.Information("[BOCCHI][財寶箱讓位] 打完自動返回：等財寶箱時 EurekaHelper 又開始找財寶（多半是第二處財寶）：先不送返回，等它找完。");
                Advance(now, true, true);
                return;
            }

            AdvanceChest(now);
            return;
        }

        var available = potHuntIpc.TryRead(out var active);
        Advance(now, available, active);
    }

    // 尋寶結束後等使用者開財寶箱；只在框架執行緒、只在這一幀讀物件表。
    private void AdvanceChest(long now)
    {
        var verdict = potWait.StepChest(now, Automator.Automator.PotChestBusy(), SecondTreasureNotice.Seen, SecondTreasureNotice.SeenAt, out var ev, out var heldMs);
        if (ev == PotChestSettleEvent.SecondTreasure)
        {
            Svc.Log.Information($"[BOCCHI][財寶箱讓位] 打完自動返回：收到「第二處財寶」（尋寶結束後 {LedgerRules.Seconds(heldMs)} 秒）：先不送返回，"
                                + $"等 EurekaHelper 開始找第二處（最多 {PotChestSettle.SecondTreasureWaitMs / 1000} 秒）。");
        }

        if (verdict != PotReturnVerdict.ReturnNow)
        {
            return;
        }

        var waited = ev switch
        {
            PotChestSettleEvent.CapReached => $"等開財寶箱滿 {PotChestSettle.CapMs / 1000} 秒（附近還有財寶箱或還在開箱）",
            PotChestSettleEvent.SecondTreasureExpired => $"收到「第二處財寶」後 {PotChestSettle.SecondTreasureWaitMs / 1000} 秒 EurekaHelper 都沒開始找（共等了 {LedgerRules.Seconds(heldMs)} 秒）",
            _ => $"附近沒有魔法甕財寶箱、也沒在開箱已 {(potWait.ChestSeen ? PotChestSettle.QuietAfterChestMs : PotChestSettle.QuietMs) / 1000} 秒（共等了 {LedgerRules.Seconds(heldMs)} 秒，"
                 + $"{(potWait.ChestSeen ? "期間看過財寶箱或開箱" : "期間沒看到財寶箱")}）",
        };
        var blocker = DeferredReturnBlocker();
        Svc.Log.Information($"[BOCCHI][財寶箱讓位] 打完自動返回：{waited}：{(blocker == null ? "送返回" : $"不送返回（{blocker}）")}。");
        ClearPotWait();
        if (blocker == null)
        {
            Return();
        }
    }

    private void Advance(long now, bool available, bool active)
    {
        var listed = potWaitFateId != 0 && potWait.IsConfirming && IsFateListed(potWaitFateId);
        var verdict = potWait.Step(now, listed, available, active, out var ev, out var heldMs);
        if (ev == PotHuntEvent.HuntStarted)
        {
            Svc.Log.Information($"[BOCCHI][魔法甕尋寶] 打完自動返回：EurekaHelper 在找魔法甕的財寶（會自己移動）：{potWaitTrigger}結束後先不送返回，等它找完（最多 {PotHuntYield.HuntCapMs / 1000} 秒）。");
        }

        if (verdict is PotReturnVerdict.ReturnNow or PotReturnVerdict.ReturnNotEnded)
        {
            FinishPotWait(verdict, ev, heldMs);
        }
    }

    private void FinishPotWait(PotReturnVerdict verdict, PotHuntEvent ev, long heldMs)
    {
        var blocker = DeferredReturnBlocker();
        var settle = blocker == null && verdict == PotReturnVerdict.ReturnNow && ev == PotHuntEvent.HuntEnded;
        var outcome = blocker != null ? $"不送返回（{blocker}）" : settle ? "先等你開財寶箱" : "送返回";
        var seconds = LedgerRules.Seconds(heldMs);
        var what = verdict == PotReturnVerdict.ReturnNotEnded
            ? $"離開{potWaitTrigger} {seconds} 秒後它仍在 F.A.T.E. 清單上（只是走出範圍，不是打完）"
            : ev switch
            {
                PotHuntEvent.HuntEnded => $"EurekaHelper 尋寶結束（等了 {seconds} 秒）",
                PotHuntEvent.GraceExpired => $"魔法甕結束後 {seconds} 秒內 EurekaHelper 沒有開始尋寶",
                PotHuntEvent.CapReached => $"等 EurekaHelper 尋寶超過 {PotHuntYield.HuntCapMs / 1000} 秒（它這一輪還沒結束）",
                PotHuntEvent.Lost => $"等尋寶途中問不到 EurekaHelper（等了 {seconds} 秒）",
                _ => "問不到 EurekaHelper，或它已經不在尋寶",
            };
        Svc.Log.Information($"[BOCCHI][魔法甕尋寶] 打完自動返回：{what}：{outcome}。");
        if (settle)
        {
            potWait.ArmChest(Environment.TickCount64);
            Svc.Log.Information($"[BOCCHI][財寶箱讓位] 打完自動返回：魔法甕尋寶結束，先不送返回，等你開挖出來的財寶箱（{PotChestSettle.NearbyM:F0} 米內還有財寶箱、"
                                + $"或你在開箱／對話中就繼續等；連續 {PotChestSettle.QuietMs / 1000} 秒都沒有才送，看過財寶箱之後改等 {PotChestSettle.QuietAfterChestMs / 1000} 秒"
                                + $"（等「第二處財寶」），最多等 {PotChestSettle.CapMs / 1000} 秒）。");
            return;
        }

        ClearPotWait();
        if (blocker == null)
        {
            Return();
        }
    }

    // 等的途中這些條件都會被改：送之前照觸發時的同一套條件再看一次。
    private string? DeferredReturnBlocker()
    {
        if (!ReturnArmed)
        {
            return "打完自動返回已停止";
        }

        if (module.GetModule<AutomatorModule>().IsEnabled)
        {
            return "非法模式開著";
        }

        if (MovingAutomation() is { } moving)
        {
            return $"「{moving}」正在進行";
        }

        var config = module.Config;
        return (pendingAfterFate && config.ReturnAfterFate) || (pendingAfterCriticalEncounter && config.ReturnAfterCriticalEncounter) ? null : "對應的打完返回設定已關";
    }

    // 非法模式以外會自己移動的自動化（巡迴、自動打怪）在跑時返回讓給它：巡迴有自己的「回營地」一步，同時送會互相打斷。
    private string? MovingAutomation()
    {
        var automation = module.Plugin.Automation;
        if (automation == null)
        {
            return null;
        }

        foreach (var kind in AutomationKindExtensions.All)
        {
            if (ReturnAfterEventYield.YieldsTo(kind) && automation.IsRunning(kind))
            {
                return AutomationControl.GetName(kind);
            }
        }

        return null;
    }

    private void CancelPotWait(string reason)
    {
        if (!potWait.IsPending)
        {
            return;
        }

        Svc.Log.Information($"[BOCCHI][魔法甕尋寶] 打完自動返回：{reason}：不再等 EurekaHelper 尋寶，不送返回。");
        ClearPotWait();
    }

    private void ClearPotWait()
    {
        potWait.Cancel();
        potWaitFateId = 0;
        potWaitTrigger = string.Empty;
        pendingAfterFate = false;
        pendingAfterCriticalEncounter = false;
        nextPotWaitPollAt = 0;
    }

    private static string EventLabel(bool afterFate, uint fateId, string fateName)
    {
        if (!afterFate)
        {
            return "緊急遭遇戰";
        }

        var name = fateName.Length > 0 ? fateName : $"#{fateId}";
        return PotCycle.IsPot(fateId) ? $"魔法甕「{name}」" : $"F.A.T.E.「{name}」";
    }

    private bool IsFateListed(uint id)
    {
        return TryGetFates() is { } tracked && tracked.fates.ContainsKey(id);
    }

    private FatesModule? TryGetFates()
    {
        if (!fatesResolved)
        {
            fatesResolved = true;
            module.TryGetModule(out fates);
        }

        return fates;
    }

    // 只在進入 F.A.T.E. 的那一幀讀，位址當場讀完即丟。
    private static unsafe uint ReadCurrentFateId()
    {
        var manager = FateManager.Instance();
        return manager == null || manager->CurrentFate == null ? 0u : manager->CurrentFate->FateId;
    }

    public void Return()
    {
        if (ZoneData.IsInForkedTower())
        {
            return;
        }

        Plugin.Chain.Submit(ChainHelper.ReturnChain());
    }

    public bool IsReady()
    {
        return module.TryGetIPCSubscriber<Lifestream>(out _);
    }
}
