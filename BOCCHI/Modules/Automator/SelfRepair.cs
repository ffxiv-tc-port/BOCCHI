using System;
using BOCCHI.Data;
using BOCCHI.Modules.Teleporter;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using ECommons;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Ocelot.IPC;

namespace BOCCHI.Modules.Automator;

/// <summary>
/// 非法模式「耐久度低於門檻時自己修理」：兩個活動之間用「修理」→「全部修理」→確認「是」，花黑暗物質修身上的裝備。
/// 只在 framework 執行緒、由 <see cref="Automator"/> 在沒有活動、沒有動作鏈時呼叫；停止（含急停）時 <see cref="Reset"/>，之後不再按任何東西。
/// </summary>
/// <remarks>
/// 每一步都重新以名稱取視窗、確認就緒才動；每次修理每個視窗最多按一下。
/// 確認框在 PreSetup 比中 Addon#863（自己修理）的問句才請 YesAlready 暫停並接手那一扇（暫停失敗就交還、不按）；認不出就不暫停、YesAlready 照常判斷。
/// 「全部修理」按鈕的指標要先在視窗自己的節點表裡找得到才按：結構偏移對不上時不按，而不是去解一個錯的指標。
/// </remarks>
public sealed class SelfRepair : IDisposable
{
    private const string RepairAddon = "Repair";

    private const string YesnoAddon = SelectYesnoSetup.AddonName;

    private const uint RepairGeneralAction = 6;

    private const long CheckIntervalMs = 2_000;

    private const long AfterSuccessIntervalMs = 30_000;

    private const long CombatSettleMs = 3_000;

    private const long ConfirmTimeoutMs = 4_000;

    private const long RepairTimeoutMs = 10_000;

    private const long RepairSettleMs = 500;

    private const long FailureBackoffMs = 300_000;

    private const long CannotRepairBackoffMs = 600_000;

    private const int YesAlreadyPauseMs = 3_000;

    private enum Step
    {
        Idle,
        Opening,
        AwaitingConfirm,
        Repairing,
    }

    private Step step;

    private long stepDeadline;

    private long nextCheckAt;

    private long backoffUntil;

    private long lastCombatAt;

    private int lowestBefore;

    private bool sawRepairing;

    private long repairingEndedAt;

    private YesAlreadyBotherPause.Result yesAlready;

    private readonly YesnoClaim claim = new();

    // 按了「全部修理」之後，PreSetup 對跳出的確認框下的結論：沒接手的原因（null＝還沒看到確認框，或已接手）。
    private string? notClaimedReason;

    private bool notClaimedCloseWindow;

    private bool cannotRepairReported;

    private RepairPrompt? prompts;

    private readonly RepairButtonGate buttonGate = new();

    // 「全部修理」所在節點的 id 與型別（記錄用）：放棄時寫進記錄，分辨「還沒變成可按」與「指到別的元件」。
    private uint buttonNodeId;

    private ushort buttonNodeType;

    public SelfRepair()
    {
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreSetup, YesnoAddon, OnYesnoPreSetup);
    }

    public void Dispose()
    {
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreSetup, YesnoAddon, OnYesnoPreSetup);
    }

    public void NoteCombat()
    {
        lastCombatAt = Environment.TickCount64;
    }

    /// <summary>停止非法模式時呼叫（冪等）：放棄進行中的修理、不再按任何按鈕；已開的視窗留給使用者。</summary>
    public void Reset(string reason)
    {
        if (step != Step.Idle)
        {
            Svc.Log.Information($"[BOCCHI][自動修理] 修理進行到一半就停止（{reason}）：之後不再按任何按鈕，開著的視窗留給你自己處理。");
        }

        step = Step.Idle;
        nextCheckAt = 0;
        backoffUntil = 0;
        cannotRepairReported = false;
        ClearConfirm();
    }

    /// <returns>true＝這一幀修理在進行中，非法模式先不選下一個活動。</returns>
    public bool Tick(AutomatorModule module, VNavmesh vnav)
    {
        var now = Environment.TickCount64;
        if (!module.Config.ShouldAutoRepair)
        {
            if (step != Step.Idle)
            {
                Reset("設定已關閉");
            }

            return false;
        }

        return step switch
        {
            Step.Idle => TryStart(module, vnav, now),
            Step.Opening => PressRepairAll(now),
            Step.AwaitingConfirm => Confirm(now),
            _ => WaitForRepair(module, now),
        };
    }

    private unsafe bool TryStart(AutomatorModule module, VNavmesh vnav, long now)
    {
        if (now < backoffUntil || now < nextCheckAt)
        {
            return false;
        }

        nextCheckAt = now + CheckIntervalMs;
        var threshold = module.Config.AutoRepairThreshold;
        if (!RepairInventory.TryScan(threshold, out var scan) || scan.Below == 0)
        {
            cannotRepairReported = false;
            return false;
        }

        if (scan.SelfRepairableBelow == 0)
        {
            if (!cannotRepairReported)
            {
                cannotRepairReported = true;
                Svc.Log.Information($"[BOCCHI][自動修理] 最低耐久度 {scan.LowestPercent}%（門檻 {threshold}%），但低於門檻的 {scan.Below} 件都不能自己修（{scan.Reason}）：不自動修理，10 分鐘後再看。");
            }

            backoffUntil = now + CannotRepairBackoffMs;
            return false;
        }

        if (!IsSafeToStart(now, vnav))
        {
            return false;
        }

        cannotRepairReported = false;
        lowestBefore = scan.LowestPercent;
        Svc.Log.Information($"[BOCCHI][自動修理] 最低耐久度 {scan.LowestPercent}%（門檻 {threshold}%，可以自己修的 {scan.SelfRepairableBelow}/{scan.Below} 件）：開啟修理視窗，用黑暗物質自己修理。");
        if (!ActionManager.Instance()->UseAction(ActionType.GeneralAction, RepairGeneralAction))
        {
            Fail("遊戲不接受「修理」（可能現在不能修理）", FailureBackoffMs, false, now);
            return false;
        }

        step = Step.Opening;
        buttonGate.Begin(now);
        buttonNodeId = 0;
        buttonNodeType = 0;
        return true;
    }

    private unsafe bool PressRepairAll(long now)
    {
        var ready = TryGetReady(RepairAddon, out var addon);
        var enabled = false;
        if (ready)
        {
            var repair = (AddonRepair*)addon;
            if (!IsOwnComponent(addon, (AtkComponentBase*)repair->RepairAllButton, out buttonNodeId, out buttonNodeType))
            {
                Fail("在修理視窗裡找不到「全部修理」按鈕（視窗結構可能和插件認定的不同），不按", CannotRepairBackoffMs, true, now);
                return false;
            }

            enabled = GenericHelpers.IsComponentEnabled(repair->RepairAllButton);
        }

        switch (buttonGate.Observe(now, ready, enabled))
        {
            case RepairButtonVerdict.Wait:
                return true;
            case RepairButtonVerdict.WindowTimeout:
                Fail($"修理視窗沒有打開（送出「修理」後 {RepairButtonGate.OpenTimeoutMs / 1000} 秒內都沒就緒）", FailureBackoffMs, false, now);
                return false;
            case RepairButtonVerdict.ButtonTimeout:
                Fail($"「全部修理」不能按（沒有需要或可以自己修的裝備）：視窗就緒後等了 {RepairButtonGate.ButtonTimeoutMs / 1000} 秒都沒有連續 {RepairButtonGate.StableFrames} 拍可按"
                     + $"（視窗就緒 {buttonGate.ReadyFrames} 拍、可按 {buttonGate.EnabledFrames} 拍、最長連續 {buttonGate.BestStreak} 拍，視窗現在{(buttonGate.WindowReadyNow ? "開著" : "已關")}，"
                     + $"按鈕節點 id={buttonNodeId} 型別={buttonNodeType}）", CannotRepairBackoffMs, true, now);
                return false;
        }

        Svc.Log.Information($"[BOCCHI][自動修理] 「全部修理」連續 {buttonGate.Streak} 拍可按：視窗在送出「修理」後 {buttonGate.OpenedAfterMs} 毫秒就緒、"
                            + $"就緒後 {buttonGate.SinceReadyMs(now)} 毫秒（{buttonGate.ReadyFrames} 拍）按下（按鈕節點 id={buttonNodeId} 型別={buttonNodeType}）。");
        ClearConfirm();
        new AddonMaster.Repair(addon).RepairAll();
        step = Step.AwaitingConfirm;
        stepDeadline = now + ConfirmTimeoutMs;
        return true;
    }

    private unsafe bool Confirm(long now)
    {
        if (notClaimedReason != null)
        {
            var reason = notClaimedReason;
            var close = notClaimedCloseWindow;
            ClearConfirm();
            Fail(reason, FailureBackoffMs, close, now);
            return false;
        }

        if (now > stepDeadline)
        {
            var claimed = claim.IsActive;
            ClearConfirm();
            Fail(claimed ? "認出的確認框在時限內一直沒準備好" : "按了「全部修理」之後確認框沒有出現", FailureBackoffMs, true, now);
            return false;
        }

        if (!claim.IsActive || !SelectYesnoSetup.TryGetReadyAt(claim.Address, out var yesno))
        {
            return true;
        }

        var prompt = GenericHelpers.GetAtkValueTextOrNull(yesno, 0);
        var same = claim.Matches((nint)yesno, prompt) && IsSelfRepairPrompt(prompt);
        ClearConfirm();
        if (!same)
        {
            Fail($"跳出的確認框不是自己修理的確認句，不代按：{prompt}", FailureBackoffMs, true, now);
            return false;
        }

        if (!TryGetReady(RepairAddon, out _))
        {
            Fail("修理視窗已經關了，不代按確認框", FailureBackoffMs, false, now);
            return false;
        }

        yesno->FireCallbackInt(0);
        Svc.Log.Information($"[BOCCHI][自動修理] 按下確認框的「是」（YesAlready：{yesAlready}）：{prompt}");
        step = Step.Repairing;
        stepDeadline = now + RepairTimeoutMs;
        sawRepairing = false;
        repairingEndedAt = 0;
        return true;
    }

    private void OnYesnoPreSetup(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonSetupArgs setup)
        {
            return;
        }

        var address = setup.Addon.Address;
        if (claim.NoteSetup(address))
        {
            Svc.Log.Information("[BOCCHI][自動修理] 接手的確認框還沒按就關了（同一位置又開了一扇確認框），不代按那一扇。");
        }

        if (step != Step.AwaitingConfirm || claim.IsActive || notClaimedReason != null)
        {
            return;
        }

        var prompt = SelectYesnoSetup.ReadPrompt(setup);
        if (!IsSelfRepairPrompt(prompt))
        {
            SetNotClaimed($"跳出的確認框不是自己修理的確認句，不代按（YesAlready 照常判斷）：{prompt}", true);
            return;
        }

        yesAlready = YesAlreadyBotherPause.Pause(YesnoAddon, YesAlreadyPauseMs);
        if (yesAlready == YesAlreadyBotherPause.Result.Failed)
        {
            SetNotClaimed($"認出修理確認框，但無法請 YesAlready 暫停（可能跟它同時按到同一個視窗），不代按、確認框與修理視窗留給你或 YesAlready：{prompt}", false);
            return;
        }

        claim.Claim(address, prompt!, Environment.TickCount64 + ConfirmTimeoutMs);
        Svc.Log.Information($"[BOCCHI][自動修理] 修理確認框建立前就認出（YesAlready：{yesAlready}，它不會碰這一扇），下一幀按「是」：{prompt}");
    }

    private void SetNotClaimed(string reason, bool closeWindow)
    {
        notClaimedReason = reason;
        notClaimedCloseWindow = closeWindow;
    }

    private void ClearConfirm()
    {
        claim.Clear();
        notClaimedReason = null;
        notClaimedCloseWindow = false;
    }

    private bool WaitForRepair(AutomatorModule module, long now)
    {
        if (Svc.Condition[ConditionFlag.Occupied39])
        {
            sawRepairing = true;
            repairingEndedAt = 0;
        }
        else if (sawRepairing && repairingEndedAt == 0)
        {
            repairingEndedAt = now;
        }

        var settled = repairingEndedAt != 0 && now - repairingEndedAt >= RepairSettleMs;
        if (!settled && now <= stepDeadline)
        {
            return true;
        }

        CloseRepairWindow();
        step = Step.Idle;
        var threshold = module.Config.AutoRepairThreshold;
        if (RepairInventory.TryScan(threshold, out var after) && after.SelfRepairableBelow == 0)
        {
            nextCheckAt = now + AfterSuccessIntervalMs;
            Svc.Log.Information($"[BOCCHI][自動修理] 修理完成：最低耐久度 {lowestBefore}% → {after.LowestPercent}%。");
            return false;
        }

        backoffUntil = now + CannotRepairBackoffMs;
        Svc.Log.Information($"[BOCCHI][自動修理] 修理後仍有可以自己修的裝備低於門檻（{(after.Below > 0 ? $"最低 {after.LowestPercent}%" : "讀不到裝備欄")}{(sawRepairing ? string.Empty : "，沒看到修理動作")}）：10 分鐘內不再自動修理。");
        return false;
    }

    private unsafe bool IsSafeToStart(long now, VNavmesh vnav)
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null || player.IsDead || ZoneData.IsInForkedTowerNoAlloc(player) || now - lastCombatAt < CombatSettleMs)
        {
            return false;
        }

        var condition = Svc.Condition;
        if (condition[ConditionFlag.InCombat]
            || condition[ConditionFlag.Mounted]
            || condition[ConditionFlag.Unconscious]
            || condition[ConditionFlag.Casting]
            || condition[ConditionFlag.Casting87]
            || condition[ConditionFlag.Jumping]
            || condition[ConditionFlag.Jumping61]
            || condition[ConditionFlag.Occupied]
            || condition[ConditionFlag.Occupied30]
            || condition[ConditionFlag.Occupied33]
            || condition[ConditionFlag.Occupied38]
            || condition[ConditionFlag.Occupied39]
            || condition[ConditionFlag.OccupiedInEvent]
            || condition[ConditionFlag.OccupiedInQuestEvent]
            || condition[ConditionFlag.OccupiedInCutSceneEvent]
            || condition[ConditionFlag.BetweenAreas]
            || condition[ConditionFlag.BetweenAreas51])
        {
            return false;
        }

        // 已經開著的修理視窗或確認框是別人的（你自己或別的插件），不碰。
        if (TryGetReady(RepairAddon, out _) || TryGetReady(YesnoAddon, out _))
        {
            return false;
        }

        try
        {
            return !vnav.IsRunning();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Fail(string reason, long backoffMs, bool closeWindow, long now)
    {
        Svc.Log.Information($"[BOCCHI][自動修理] {reason}；{backoffMs / 60_000} 分鐘內不再自動修理。");
        if (closeWindow)
        {
            CloseRepairWindow();
        }

        step = Step.Idle;
        backoffUntil = now + backoffMs;
    }

    private static unsafe void CloseRepairWindow()
    {
        if (TryGetReady(RepairAddon, out var addon))
        {
            addon->Close(true);
        }
    }

    private static unsafe bool TryGetReady(string name, out AtkUnitBase* addon)
    {
        return GenericHelpers.TryGetAddonByName(name, out addon) && GenericHelpers.IsAddonReady(addon);
    }

    // 只比對指標、不解參考 component：它不在這個視窗的節點表裡就當作沒有。
    private static unsafe bool IsOwnComponent(AtkUnitBase* addon, AtkComponentBase* component, out uint nodeId, out ushort nodeType)
    {
        nodeId = 0;
        nodeType = 0;
        if (component == null || addon->UldManager.NodeList == null)
        {
            return false;
        }

        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node != null && (ushort)node->Type >= 1000 && ((AtkComponentNode*)node)->Component == component)
            {
                nodeId = node->NodeId;
                nodeType = (ushort)node->Type;
                return true;
            }
        }

        return false;
    }

    private bool IsSelfRepairPrompt(string? prompt)
    {
        if (prompts == null)
        {
            var sheet = Svc.Data.GetExcelSheet<Addon>();
            prompts = new RepairPrompt(sheet.GetRowOrDefault(RepairPrompt.SelfRepairRow)?.Text, sheet.GetRowOrDefault(RepairPrompt.MenderRow)?.Text);
            Svc.Log.Information(prompts.IsAvailable
                ? $"[BOCCHI][自動修理] 修理確認句範本（Addon#{RepairPrompt.SelfRepairRow}）：{string.Join(" … ", prompts.Segments)}"
                : $"[BOCCHI][自動修理] 讀不到 Addon#{RepairPrompt.SelfRepairRow} 的修理確認句，確認框一律不代按。");
        }

        return prompts.IsSelfRepairPrompt(prompt);
    }
}
