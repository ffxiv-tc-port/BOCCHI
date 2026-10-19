using System;
using System.Collections.Generic;
using BOCCHI.Data;
using BOCCHI.Modules.StateManager;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Ocelot.Chain;

namespace BOCCHI.Modules.PhantomJobs;

/// <summary>
/// 輔助職業練等：目前的輔助職業滿級（或是不靠經驗值升級的輔助自由人）時，切到下一個已取得、還沒滿級的。
/// 開始與停止一律經過 <see cref="BOCCHI.Automation.AutomationControl"/>；只在 framework 執行緒使用。
/// </summary>
/// <remarks>
/// 只在「沒有戰鬥、不在 F.A.T.E.／緊急遭遇戰、BOCCHI 沒有任何動作鏈在跑」時切換，不跟增益／尋寶／戰鬥之鈴等自己會換職業的動作鏈搶。
/// 切換走遊戲自己的 ChangeSupportJob（BOCCHI 既有的增益、尋寶動作鏈用的是同一支），送出後只看目前職業有沒有變，不重送。
/// </remarks>
public sealed class PhantomJobLeveling
{
    private const long SwitchTimeoutMs = 5_000;

    private const long FailureBackoffMs = 60_000;

    private const long MissingFunctionBackoffMs = 600_000;

    // 戰鬥剛結束時遊戲常拒絕切換。
    private const long CombatSettleMs = 4_000;

    private readonly byte[] levels = new byte[PhantomJobTable.Count];

    private int pendingTarget = -1;

    private int pendingFrom = -1;

    private long pendingDeadline;

    private long backoffUntil;

    private long lastCombatAt;

    private bool nothingToLevelReported;

    public bool IsRunning { get; private set; }

    /// <summary>當幀：目前的輔助職業該換、有下一個、不在失敗後的等待期。</summary>
    public bool WantsSwitch { get; private set; }

    /// <summary>已送出切換、還在等遊戲換過去。</summary>
    public bool IsSwitching
    {
        get => pendingTarget >= 0;
    }

    /// <summary>下一個要練的輔助職業；不必換、找不到或讀不到時是 -1。</summary>
    public int NextTarget { get; private set; } = -1;

    public void Start()
    {
        ResetRun();
        IsRunning = true;
    }

    /// <summary>停止並清掉所有狀態（冪等）。已送出的切換無法收回，但不會再送新的。</summary>
    public void ResetRun()
    {
        IsRunning = false;
        WantsSwitch = false;
        NextTarget = -1;
        pendingTarget = -1;
        pendingFrom = -1;
        backoffUntil = 0;
        nothingToLevelReported = false;
    }

    public void Tick(StateManagerModule? states)
    {
        if (!IsRunning)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (Svc.Condition[ConditionFlag.InCombat])
        {
            lastCombatAt = now;
        }

        var current = PhantomJobTable.ReadLevels(levels);
        if (current < 0)
        {
            WantsSwitch = false;
            NextTarget = -1;
            return;
        }

        if (pendingTarget >= 0)
        {
            WantsSwitch = false;
            CheckPending(current, now);
            return;
        }

        var max = PhantomJobTable.MaxLevels;
        var next = PhantomJobLevelingRules.FindNext(levels, max, current);
        NextTarget = next;
        if (next < 0)
        {
            WantsSwitch = false;
            if (PhantomJobLevelingRules.ShouldSwitchFrom(levels[current], max[current]) && !nothingToLevelReported)
            {
                nothingToLevelReported = true;
                Svc.Log.Information($"[BOCCHI][輔助職業練等] 目前的「{PhantomJobTable.GetName(current)}」不用再練（Lv {levels[current]}/{max[current]}），但沒有其他已取得、還沒滿級的輔助職業可以切換：維持不動。");
            }

            return;
        }

        nothingToLevelReported = false;
        if (now < backoffUntil)
        {
            WantsSwitch = false;
            return;
        }

        WantsSwitch = true;
        if (!CanSwitchNow(states, now))
        {
            return;
        }

        Switch(current, next, now);
    }

    private unsafe void Switch(int current, int next, long now)
    {
        var max = PhantomJobTable.MaxLevels;
        if (PublicContentOccultCrescent.MemberFunctionPointers.ChangeSupportJob == null)
        {
            backoffUntil = now + MissingFunctionBackoffMs;
            WantsSwitch = false;
            Svc.Log.Information("[BOCCHI][輔助職業練等] 找不到遊戲切換輔助職業的函式（特徵碼對不上），不切換；10 分鐘後再試。");
            return;
        }

        Svc.Log.Information($"[BOCCHI][輔助職業練等] 「{PhantomJobTable.GetName(current)}」Lv {levels[current]}/{max[current]} 不用再練：切換到「{PhantomJobTable.GetName(next)}」（Lv {levels[next]}/{max[next]}）。");
        try
        {
            PublicContentOccultCrescent.ChangeSupportJob((byte)next);
        }
        catch (Exception ex)
        {
            backoffUntil = now + FailureBackoffMs;
            WantsSwitch = false;
            Svc.Log.Information($"[BOCCHI][輔助職業練等] 切換失敗（{ex.GetType().Name}）：{ex.Message}；60 秒後再試。");
            return;
        }

        pendingTarget = next;
        pendingFrom = current;
        pendingDeadline = now + SwitchTimeoutMs;
        WantsSwitch = false;
    }

    private void CheckPending(int current, long now)
    {
        if (current == pendingTarget)
        {
            Svc.Log.Information($"[BOCCHI][輔助職業練等] 已切換到「{PhantomJobTable.GetName(current)}」。");
            pendingTarget = -1;
            pendingFrom = -1;
            return;
        }

        if (now <= pendingDeadline)
        {
            return;
        }

        Svc.Log.Information($"[BOCCHI][輔助職業練等] 送出切換到「{PhantomJobTable.GetName(pendingTarget)}」後 {SwitchTimeoutMs / 1000} 秒仍是「{PhantomJobTable.GetName(current)}」（遊戲可能拒絕了這次切換）；60 秒後再試。");
        if (current != pendingFrom)
        {
            Svc.Log.Information("[BOCCHI][輔助職業練等] 這段期間輔助職業被換成別的（你自己或其他功能換的），以目前的為準。");
        }

        pendingTarget = -1;
        pendingFrom = -1;
        backoffUntil = now + FailureBackoffMs;
    }

    private bool CanSwitchNow(StateManagerModule? states, long now)
    {
        if (now - lastCombatAt < CombatSettleMs || states == null || states.GetState() != State.Idle)
        {
            return false;
        }

        var player = Svc.Objects.LocalPlayer;
        if (player == null || player.IsDead || ZoneData.IsInForkedTowerNoAlloc(player))
        {
            return false;
        }

        var condition = Svc.Condition;
        if (condition[ConditionFlag.InCombat]
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

        return !AnyChainActive();
    }

    // 只在「該換」的時候才走到這裡；直接走具體的 Dictionary 才不會每次配置列舉器。
    private static bool AnyChainActive()
    {
        if (ChainManager.Queues is not Dictionary<string, ChainQueue> queues)
        {
            return true;
        }

        foreach (var pair in queues)
        {
            if (pair.Value.IsRunning || pair.Value.QueueCount > 0)
            {
                return true;
            }
        }

        return false;
    }
}
