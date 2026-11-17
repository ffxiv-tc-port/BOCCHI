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

public sealed partial class AutomationControl
{
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
            parts.Add(StopOwnPath());
            navigationGuardUntil = Environment.TickCount64 + NavigationGuardMs;
            navigationGuardFired = false;
            guardForPotHunt = true;
        }

        AbortLifestreamIfBusy(parts);
        return string.Join("、", parts);
    }

    // 呼叫前 guardDestinations 已經 CaptureCurrent：vnavmesh 正在走的路線終點是其中之一才停（讀不到終點照舊停）。回傳給 log 用的一句話。
    private string StopOwnPath()
    {
        var verdict = JudgeCurrentPath(guardDestinations, out var end);
        if (verdict is OwnPathVerdict.Own or OwnPathVerdict.Unknown)
        {
            return TryInvoke("Path.Stop（BOCCHI 自己的路線）", () => vnav!.Stop());
        }

        return verdict == OwnPathVerdict.Idle
            ? "vnavmesh 沒在走"
            : $"vnavmesh 正在走別人的路線（終點 {FormatPoint(end)}，離 BOCCHI 送過的目的地 {FormatDistance(guardDestinations.NearestXZ(end!.Value))} 米）：不停，交給送出它的插件或你自己";
    }

    private void AbortLifestreamIfBusy(List<string> parts)
    {
        if (lifestream == null || !lifestream.IsReady())
        {
            return;
        }

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

    /// <summary>vnavmesh 現在走的路線是不是 <paramref name="own"/> 裡的目的地；<paramref name="end"/>＝路線終點（讀不到是 null）。</summary>
    private OwnPathVerdict JudgeCurrentPath(OwnMoveDestinations own, out System.Numerics.Vector3? end)
    {
        if (vnav == null)
        {
            end = null;
            return OwnPathVerdict.Idle;
        }

        return OwnPathStop.Judge(vnav, own, out end);
    }

    private static string FormatPoint(System.Numerics.Vector3? point)
    {
        return point is { } p ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"({p.X:F1}, {p.Y:F1}, {p.Z:F1})") : "?";
    }

    // 使用者停掉一項：導航照舊全域停（含別的插件或你自己送的路線）。
    private bool StopOneByUser(AutomationKind kind, string reason, bool announce, bool wasRunning)
    {
        if (stopping)
        {
            return false;
        }

        stopping = true;
        try
        {
            if (!ResetOne(kind, reason, announce, wasRunning, false, out var returnCast))
            {
                return false;
            }

            var navigation = kind switch
            {
                AutomationKind.ReturnAfterEvents => AbortForReturn(),
                AutomationKind.PhantomJobLeveling => "不涉及移動",
                _ => AbortForMovementKindByUser(kind),
            };
            ReportStopped(kind, reason, announce, returnCast, navigation);
            return true;
        }
        finally
        {
            stopping = false;
        }
    }

    // 巡迴自己停下：進度保留成暫停，vnavmesh 只停 BOCCHI 自己的路線。
    private bool StopOneKeepingProgress(AutomationKind kind, string reason, bool announce, bool wasRunning)
    {
        if (stopping)
        {
            return false;
        }

        stopping = true;
        try
        {
            if (!ResetOne(kind, reason, announce, wasRunning, true, out var returnCast))
            {
                return false;
            }

            var navigation = kind == AutomationKind.PhantomJobLeveling ? "不涉及移動" : AbortForMovementKindOwn(kind, true);
            ReportStopped(kind, reason, announce, returnCast, navigation);
            return true;
        }
        finally
        {
            stopping = false;
        }
    }

    // 停一項的狀態部分（導航由呼叫端依來源收）。回 false＝原本就沒在跑：只做收尾，不收導航也不報告。
    private bool ResetOne(AutomationKind kind, string reason, bool announce, bool wasRunning, bool keepProgress, out bool returnCast)
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

        returnCast = ReturnChain.OwnCastInFlight;
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

        return true;
    }

    private void ReportStopped(AutomationKind kind, string reason, bool announce, bool returnCast, string navigation)
    {
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

    // 使用者停止／暫停／換一場：導航照舊全域停，忙碌中的 Lifestream 一起中止。
    private string AbortForMovementKindByUser(AutomationKind kind)
    {
        AbortQueuesForKind(kind);
        return StopNavigationByUser();
    }

    // 自動化自己換目標或停下：vnavmesh 只停 BOCCHI 自己的路線。
    // abortLifestream＝false：非法模式自己換目標，Lifestream 忙的話是使用者的（在地圖上點了目的地），不中止它。
    private string AbortForMovementKindOwn(AutomationKind kind, bool abortLifestream)
    {
        AbortQueuesForKind(kind);
        return StopOwnNavigation(abortLifestream);
    }

    private void AbortQueuesForKind(AutomationKind kind)
    {
        var queue = kind switch
        {
            AutomationKind.MobFarmer => Farmer.QueueName,
            AutomationKind.TreasureHunt => treasure?.Hunter?.StepQueueName,
            AutomationKind.CarrotHunt => carrots?.Hunter?.StepQueueName,
            _ => null,
        };

        AbortQueues(id => id == MainQueue || id == queue || id.EndsWith(WatcherSuffix, StringComparison.Ordinal));
        teleporter?.CancelReturnPrompt();
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
        return StopNavigationByUser();
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

    // 使用者要的停止（全部停止含急停、暫停、單項停止、換一場；換區／登出／倒地經全部停止）：
    // 刻意全域清掉 vnavmesh 正在走的路徑（不論是誰送的）；Lifestream 只在忙碌時叫它中止。
    private string StopNavigationByUser()
    {
        var parts = new List<string>();
        if (BeginNavigationStop(parts))
        {
            parts.Add(TryInvoke("Path.Stop", () => vnav!.Stop()));
            ArmNavigationGuard();
        }

        AbortLifestreamIfBusy(parts);
        return string.Join("、", parts);
    }

    // 自動化自己停下（非法模式換目標、巡迴自己停止、插件卸載）：vnavmesh 只停 BOCCHI 自己的路線，別人的不停（寫進回傳的那一句）。
    private string StopOwnNavigation(bool abortLifestream)
    {
        var parts = new List<string>();
        if (BeginNavigationStop(parts))
        {
            parts.Add(StopOwnPath());
            ArmNavigationGuard();
        }

        if (abortLifestream)
        {
            AbortLifestreamIfBusy(parts);
        }

        return string.Join("、", parts);
    }

    // 兩種停止共用的前半：作廢趕路登記（之後算完的迴避路徑不可以再讓角色動起來；作廢前先記下守衛要認的目的地），
    // 取消算路只在 vnavmesh 裡只有 BOCCHI 自己那筆時才叫（全域的）。回 false＝vnavmesh 未載入。
    private bool BeginNavigationStop(List<string> parts)
    {
        var vnavReady = vnav != null && vnav.IsReady();
        if (vnavReady)
        {
            guardDestinations.CaptureCurrent(TravelIntent.State);
        }

        OwnMoveDestinations.Recent.Clear();
        TravelIntent.Invalidate();
        if (!vnavReady)
        {
            parts.Add("vnavmesh 未載入");
            return false;
        }

        try
        {
            parts.Add(OwnPathfind.Describe(OwnPathfind.CancelIfOnlyOwn(vnav!, guardDestinations.Count > 0)));
        }
        catch (Exception ex)
        {
            parts.Add($"Nav.PathfindCancelAll 失敗（{ex.GetType().Name}）");
        }

        return true;
    }

    private void ArmNavigationGuard()
    {
        navigationGuardUntil = Environment.TickCount64 + NavigationGuardMs;
        navigationGuardFired = false;
        guardForPotHunt = false;
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
}
