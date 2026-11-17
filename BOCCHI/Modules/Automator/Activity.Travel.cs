using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BOCCHI.Automation;
using BOCCHI.Chains;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Ledger;
using BOCCHI.Modules.StateManager;
using BOCCHI.Pathfinding;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using Ocelot.Chain;
using Ocelot.IPC;

namespace BOCCHI.Modules.Automator;

public abstract partial class Activity
{
    /// <summary>
    /// 尋路監看就要擲例外（整條趕路鏈中止重來）之前呼叫：每個活動只寫第一次，後面幾次由活動帳的「重來」計數。
    /// 只讀狀態旗標與 IPC、不改任何判斷；只在 framework 執行緒呼叫。
    /// </summary>
    protected void ReportTravelInterrupt(string reason)
    {
        if (Record.TravelInterruptReported)
        {
            return;
        }

        Record.TravelInterruptReported = true;
        Svc.Log.Information($"[BOCCHI][趕路中斷] kind={KindLabel} id={data.Id} name=「{GetName()}」 原因={reason} {TravelStateFields()} 第={Record.PathfindingRuns}");
    }

    private string TravelStateFields()
    {
        var condition = Svc.Condition;
        var left = Vector3.Distance(Player.Position, GetPosition());
        var since = Record.PathfindingStartedAt == 0 ? "?" : ((Environment.TickCount64 - Record.PathfindingStartedAt) / 1000f).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        return $"離目的地={left:F1} 騎乘={YesNo(condition[ConditionFlag.Mounted])} "
               + $"上下坐騎中={YesNo(condition[ConditionFlag.MountOrOrnamentTransition])} 詠唱={YesNo(condition[ConditionFlag.Casting])} 戰鬥={YesNo(condition[ConditionFlag.InCombat])} "
               + $"vnav走={QueryVnav(() => vnav.IsRunning())} vnav算路={QueryVnav(() => vnav.IsSimpleMoveInProgress())} 網格算路={QueryVnav(() => vnav.IsPathfinding())} 這趟={since}";
    }

    /// <summary>上下坐騎、詠唱、跳躍、讀圖中：導航停著是暫時的，趕路監看不當成停下。</summary>
    internal static bool IsMovementPaused()
    {
        var condition = Svc.Condition;
        return condition[ConditionFlag.Mounting] || condition[ConditionFlag.Mounting71] || condition[ConditionFlag.MountOrOrnamentTransition]
               || condition[ConditionFlag.Casting] || condition[ConditionFlag.Casting87]
               || condition[ConditionFlag.Jumping] || condition[ConditionFlag.Jumping61]
               || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51];
    }

    /// <summary>
    /// 趕路監看每一拍呼叫（在動時也要呼叫，停下計時才會重來）。停下時照 <see cref="TravelStopGuard"/> 等、或在原地重送同一個目的地；
    /// 回 false＝重送用完，呼叫端照舊中止整趟重來。只在 framework 執行緒呼叫。
    /// </summary>
    protected bool KeepTravelling(TravelStopGuard guard, bool navigating, TravelSource source)
    {
        var paused = !navigating && IsMovementPaused();
        var decision = guard.Next(Vector3.Distance(Player.Position, GetPosition()), navigating, paused, Environment.TickCount64);
        if (decision == TravelDecision.Rewalk)
        {
            Record.TravelRewalks++;
            ReportOnce(ref Record.TravelRewalkReported, $"[BOCCHI][趕路重走] kind={KindLabel} id={data.Id} name=「{GetName()}」 重走第={guard.Rewalks} {TravelStateFields()}");
            SendTravel(source);
        }
        else if (decision == TravelDecision.Resume)
        {
            ReportOnce(ref Record.TravelResumeReported,
                $"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} name=「{GetName()}」 角色已靜止 {ManualMoveDetector.ResumeQuietMs / 1000f:F1} 秒：從目前位置接續前往（這個活動累計讓位 {Record.TravelYieldMs / 1000f:F1} 秒） {TravelStateFields()}");
            SendTravel(source);
        }

        return decision != TravelDecision.GiveUp;
    }

    /// <summary>
    /// 趕路監看每一拍、在每一條路徑上都呼叫（含原地等、追敵人、戰鬥暫停）：讓位取樣約每 100 毫秒一次，選項關著時什麼都不讀。
    /// 只在 framework 執行緒呼叫。
    /// </summary>
    protected void ObserveTravel(TravelStopGuard guard, bool navigating)
    {
        guard.YieldEnabled = module.Config.YieldToManualMovement;
        if (!guard.YieldEnabled)
        {
            travelHolding = false;
            guard.Manual.Reset();
            return;
        }

        var now = Environment.TickCount64;
        travelHolding = guard.HoldsOwnMoves(navigating);
        travelHoldingAt = now;
        // 不經建構子建出的物件第一次用時才建（同 Record）。按鍵每拍都讀：短按可能落在兩次取樣之間。
        handsOn ??= new HandsOnProbe();
        var input = handsOn.ReadInput(now);
        if (guard.Manual.External && navigating && OwnPathStop.Stops(StopOwnMove("[BOCCHI][趕路讓位]", "為 Lifestream 讓位中、前往", yieldStopNotice ??= new ForeignPathNotice())))
        {
            Svc.Log.Debug($"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} 為 Lifestream 讓位中 vnavmesh 開始走（BOCCHI 先前送的路算完了）：停下");
        }

        var player = Svc.Objects.LocalPlayer;
        if (!guard.ShouldSample(now) || player == null)
        {
            return;
        }

        lifestreamBusy ??= new LifestreamBusyProbe(lifestream);
        var interacting = !input && HandsOnProbe.Interacting(now);
        var step = guard.Observe(player.Position, navigating, !navigating && IsMovementPaused(), lifestreamBusy.Query(now), input || interacting, now);
        Record.TravelYieldMs += step.YieldedMs;
        if (guard.Yielding)
        {
            HandsOnRecent.Note(now, "趕路正在讓位給你操作");
        }

        switch (step.Event)
        {
            case TravelYieldEvent.Touched when guard.Manual.Capped:
                ReportOnce(ref Record.TravelCappedTouchReported,
                    $"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} name=「{GetName()}」 讓位滿 {ManualMoveDetector.MaxYieldMs / 1000} 秒後偵測到你還在操作（{(input ? "按了移動鍵" : "在互動：開寶箱、對話一類")}）："
                    + $"不重送，改成按鍵／互動讓位；沒有按鍵、沒在互動、角色靜止 {ManualMoveDetector.ResumeQuietMs / 1000f:F1} 秒後從目前位置接續；"
                    + $"這種讓位一趟累計上限 {ManualMoveDetector.TouchMaxMs / 1000} 秒 {TravelStateFields()}");
                break;
            case TravelYieldEvent.Touched:
                ReportOnce(ref Record.TravelTouchReported,
                    $"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} name=「{GetName()}」 導航停下時偵測到你在操作（{(input ? "按了移動鍵" : "在互動：開寶箱、對話一類")}）："
                    + $"不重送、不算重走次數；沒有按鍵、沒在互動、角色靜止 {ManualMoveDetector.ResumeQuietMs / 1000f:F1} 秒後從目前位置接續；"
                    + $"這種讓位一趟累計上限 {ManualMoveDetector.TouchMaxMs / 1000} 秒 {TravelStateFields()}");
                break;
            case TravelYieldEvent.TouchLimit:
                Svc.Log.Information($"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} name=「{GetName()}」 為按鍵／互動讓位累計超過 {ManualMoveDetector.TouchMaxMs / 1000} 秒："
                                    + $"這一趟之後只看持續移動，停下就照原本的重送／中止重來 {TravelStateFields()}");
                break;
            case TravelYieldEvent.External:
                var stopped = navigating && StopNavigationForLifestream();
                ReportOnce(ref Record.TravelExternalReported,
                    $"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} name=「{GetName()}」 Lifestream 正在執行任務（多半是你在地圖上點了目的地）："
                    + $"{(stopped ? "停下 BOCCHI 自己的導航、" : string.Empty)}讓位到它做完，這段時間不算進 {ManualMoveDetector.MaxYieldMs / 1000} 秒上限；"
                    + $"它做完、角色靜止 {ManualMoveDetector.ResumeQuietMs / 1000f:F1} 秒後從目前位置接續 {TravelStateFields()}");
                break;
            case TravelYieldEvent.ExternalLimit:
                Svc.Log.Information($"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} name=「{GetName()}」 為 Lifestream 讓位累計超過 {ManualMoveDetector.ExternalMaxMs / 1000} 秒："
                                    + $"這一趟之後不再為它讓位（照手動移動的規則） {TravelStateFields()}");
                break;
            case TravelYieldEvent.Started:
                ReportOnce(ref Record.TravelYieldReported,
                    $"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} name=「{GetName()}」 偵測到手動移動（導航停著、角色以 {step.Speed:F1} 米/秒持續移動 {ManualMoveDetector.ConfirmMs / 1000f:F1} 秒）："
                    + $"暫停自動趕路，角色靜止 {ManualMoveDetector.ResumeQuietMs / 1000f:F1} 秒後從目前位置接續；超過 {ManualMoveDetector.MaxYieldMs / 1000} 秒照舊中止重來 {TravelStateFields()}");
                break;
            case TravelYieldEvent.Expired:
                Svc.Log.Information($"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} name=「{GetName()}」 手動移動超過 {ManualMoveDetector.MaxYieldMs / 1000} 秒：這一趟改回原本的停下重送／中止重來 {TravelStateFields()}");
                break;
            case TravelYieldEvent.Cancelled:
                Svc.Log.Debug($"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} 讓位中導航又開始走：結束讓位、不另外接續");
                break;
        }
    }

    /// <summary>
    /// 趕路監看的任務設定：180 秒時限只算沒在讓位的時間（逾時那一刻把這段期間讓位的時間補回去）。
    /// 讓位本身另有上限（手動一次 30 秒、為 Lifestream 一趟 180 秒）：Lifestream 一直忙時最長約 360 秒，
    /// 它還一直帶角色走就再加用完後看起來像手動移動的 30 秒（約 390 秒）。
    /// </summary>
    protected TaskManagerConfiguration TravelWatcherConfig(TravelStopGuard guard)
    {
        return new TaskManagerConfiguration
        {
            TimeLimitMS = TravelWatcherLimitMs,
            ShowError = false,
            OnTaskTimeout = (TaskManagerTask _, ref long remaining) => ExtendForYield(guard, ref remaining),
        };
    }

    private const int TravelWatcherLimitMs = 180_000;

    // 任務自帶逾時處理器時 Ocelot 不印逾時警告：補回後仍不夠（真的會中止）由這裡印，等級沿用 Ocelot 的 Warning。
    private void ExtendForYield(TravelStopGuard guard, ref long remaining)
    {
        var credit = guard.TakeYieldCredit();
        remaining += credit;
        if (remaining < 0)
        {
            Svc.Log.Warning($"[BOCCHI][趕路中斷] kind={KindLabel} id={data.Id} name=「{GetName()}」 原因=趕路監看逾時（{TravelWatcherLimitMs / 1000} 秒，"
                            + $"已補回讓位 {credit / 1000f:F1} 秒）：中止這一趟、重新規劃 {TravelStateFields()} 第={Record.PathfindingRuns}");
            return;
        }

        if (credit <= 0)
        {
            return;
        }

        Svc.Log.Information($"[BOCCHI][趕路讓位] kind={KindLabel} id={data.Id} name=「{GetName()}」 趕路監看到了 {TravelWatcherLimitMs / 1000} 秒時限，"
                            + $"其中讓位 {credit / 1000f:F1} 秒不算：時限往後延 {credit / 1000f:F1} 秒 {TravelStateFields()}");
    }

    // 同 Hunter：讓位時絕不取消算路（全域的，會連 Lifestream 在算的一起取消），只停已經在走的 BOCCHI 自己的路；
    // BOCCHI 還在算的路算完開走時，讓位期間每拍再停（ObserveTravel）。回傳有沒有停下東西。
    private bool StopNavigationForLifestream()
    {
        return OwnPathStop.Stops(StopOwnMove("[BOCCHI][趕路讓位]", "為 Lifestream 讓位、前往", yieldStopNotice ??= new ForeignPathNotice()));
    }

    // 同一個活動第一次寫 Information，之後寫 Debug。
    private static void ReportOnce(ref bool reported, string line)
    {
        if (reported)
        {
            Svc.Log.Debug(line);
            return;
        }

        reported = true;
        Svc.Log.Information(line);
    }

    // 重送 PathfindingChain 登記的那個目的地（同一趟，迴避的登記不重開）；登記已作廢或不是這個來源才用活動中心。
    private void SendTravel(TravelSource source)
    {
        var intent = TravelIntent.State;
        var destination = intent.HasIntent && intent.Current.Source == source ? intent.Current.Destination : GetPosition();
        TravelIntent.Register(destination, source);
        vnav.PathfindAndMoveTo(destination, false);
    }

    private static string QueryVnav(Func<bool> query)
    {
        try
        {
            return YesNo(query());
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static string YesNo(bool value)
    {
        return value ? "是" : "否";
    }
}
