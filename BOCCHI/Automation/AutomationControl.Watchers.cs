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
    private void OnFrameworkUpdate(IFramework framework)
    {
        var external = Interlocked.Exchange(ref pendingExternalStop, null);
        if (external != null)
        {
            StopAll(ExternalReason(external), true);
        }

        WatchRevive();

        WatchConfigWindowToggle();
        GuardNavigation();
        if (automator != null)
        {
            automator.automator.PruneSkips(automator);
        }

        var anyRunning = false;
        foreach (var kind in AutomationKindExtensions.All)
        {
            var running = IsRunning(kind);
            runningLastFrame[(int)kind] = running;
            anyRunning |= running;
        }

        var farming = runningLastFrame[(int)AutomationKind.MobFarmer];
        PhantomActions.Sync(runningLastFrame[(int)AutomationKind.IllegalMode] || farming, farming);
        if (EnteredForkedTower())
        {
            Svc.Log.Information("[BOCCHI][兩歧塔] 非法模式中進了兩歧塔（設定：兩歧塔開放時到傳送魔紋等候）：全部停止，塔內交給你自己的戰鬥 AI");
            StopAll(I18N.T("automation.reason.entered_forked_tower"), true, true, ActivityEnd.EnteredForkedTower);
            return;
        }

        if (!anyRunning)
        {
            WatchDownedWhileIdle();
            return;
        }

        if (!Svc.ClientState.IsLoggedIn)
        {
            StopAll(I18N.T("automation.reason.logged_out"), false, true, ActivityEnd.LoggedOut);
            return;
        }

        if (Svc.Objects.LocalPlayer?.IsDead == true)
        {
            LogDowned();
            CaptureDeathRestore();
            StopAll(I18N.T("automation.reason.dead"), true, true, ActivityEnd.Downed);
        }
    }

    // 剛從塔外進到塔裡、設定開著、非法模式在跑時回 true。每幀呼叫；不配置。
    private bool EnteredForkedTower()
    {
        var player = Svc.Objects.LocalPlayer;
        var inside = player != null && ForkedTowerPad.IsInsideTower(player);
        var entered = inside && !insideForkedTower;
        insideForkedTower = inside;
        return entered && runningLastFrame[(int)AutomationKind.IllegalMode] && automator?.Config.ShouldWaitAtForkedTowerPad == true;
    }

    // 停止之前記下在跑的項目；只記不恢復。
    private void CaptureDeathRestore()
    {
        Span<bool> running = stackalloc bool[AutomationKindExtensions.All.Length];
        if (DeathRestoreEnabled)
        {
            foreach (var kind in AutomationKindExtensions.All)
            {
                running[(int)kind] = IsRunning(kind);
            }
        }

        // 選項關著時記一份空的：同時丟掉選項開著時留下的舊清單。
        ClearDeathRestoreText();
        if (!deathRestore.Capture(running))
        {
            return;
        }

        deathRestoreNames = string.Join("、", AutomationKindExtensions.All.Where(deathRestore.Contains).Select(GetName));
        deathRestoreLabel = I18N.T("automation.panel.death_restore", new Dictionary<string, string> { ["names"] = deathRestoreNames });
        Svc.Log.Information($"[BOCCHI][倒地恢復] 記下倒地時停下的自動化「{deathRestoreNames}」：復活時提示，按恢復才恢復");
    }

    // 復活時提示一次（聊天＋通知）；不會自己恢復。
    private void PromptDeathRestore()
    {
        if (!DeathRestoreEnabled || !deathRestore.TakePrompt())
        {
            return;
        }

        Svc.Log.Information($"[BOCCHI][倒地恢復] 已復活，提示可以恢復「{deathRestoreNames}」（主視窗的恢復鈕或 /bocchi restore；不會自己恢復）");
        var args = new Dictionary<string, string> { ["names"] = deathRestoreNames };
        Svc.Chat.Print(I18N.T("automation.messages.death_restore_prompt", args));
        try
        {
            Svc.NotificationManager.AddNotification(new Notification
            {
                Title = I18N.T("automation.messages.death_restore_notification_title"),
                Content = I18N.T("automation.messages.death_restore_notification", args),
                Type = NotificationType.Info,
                Minimized = false,
                InitialDuration = TimeSpan.FromSeconds(15),
            });
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[BOCCHI][倒地恢復] 通知沒有送出（{ex.GetType().Name}）：{ex.Message}；聊天視窗的提示照常");
        }
    }

    private void InvalidateDeathRestore(string why)
    {
        if (deathRestore.Invalidate())
        {
            Svc.Log.Information($"[BOCCHI][倒地恢復] 不再保留倒地時停下的「{deathRestoreNames}」（{why}）");
        }

        ClearDeathRestoreText();
    }

    private void ClearDeathRestoreText()
    {
        deathRestoreNames = string.Empty;
        deathRestoreLabel = string.Empty;
    }

    // 恢復時清單已先取走，所以恢復自己開始的項目不會走到作廢。
    private void InvalidateDeathRestoreOnManualStart(AutomationKind kind)
    {
        if (deathRestore.HasSnapshot)
        {
            InvalidateDeathRestore(I18N.T("automation.reason.manual_start", new Dictionary<string, string> { ["name"] = GetName(kind) }));
        }
    }

    // 沒有自動化在跑時，在新月島倒地也寫 [倒地]／[復活]（不停任何東西、不記恢復清單）。
    private void WatchDownedWhileIdle()
    {
        if (downedAt != 0 || !Svc.ClientState.IsLoggedIn || Svc.Objects.LocalPlayer?.IsDead != true || !ZoneData.IsInOccultCrescent())
        {
            return;
        }

        LogDowned();
    }

    // 倒地與復活各寫一行；復活後不會自己恢復自動化（無人值守的恢復是紅線），只記錄。
    private void LogDowned()
    {
        try
        {
            LogDownedCore();
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[BOCCHI][倒地] 寫記錄時出錯（不影響停止）：{ex.GetType().Name}：{ex.Message}");
        }
    }

    private void LogDownedCore()
    {
        if (downedAt != 0)
        {
            return;
        }

        var player = Svc.Objects.LocalPlayer;
        downedPosition = player?.Position ?? default;
        downedAt = Environment.TickCount64;
        var activity = automator?.automator.Activity;
        var running = string.Join("、", AutomationKindExtensions.All.Where(IsRunning).Select(GetName));
        downedWhileRunning = running.Length > 0;
        var label = activity == null ? "-" : $"{activity.KindLabel}#{activity.data.Id}";
        Svc.Log.Information(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[BOCCHI][倒地] 活動={label} 活動名=「{activity?.GetName() ?? "-"}」 活動狀態={activity?.state.ToString() ?? "-"} 狀態={stateManager?.GetState().ToString() ?? "?"} "
            + $"位置=({downedPosition.X:F1},{downedPosition.Y:F1},{downedPosition.Z:F1}) 自動化=「{running}」 自動化狀態={(downedWhileRunning ? "開" : "關")}"));
    }

    private void WatchRevive()
    {
        if (downedAt == 0)
        {
            return;
        }

        try
        {
            WatchReviveCore();
        }
        catch (Exception ex)
        {
            downedAt = 0;
            Svc.Log.Warning($"[BOCCHI][復活] 寫記錄時出錯：{ex.GetType().Name}：{ex.Message}");
        }
    }

    private void WatchReviveCore()
    {

        if (!Svc.ClientState.IsLoggedIn)
        {
            downedAt = 0;
            InvalidateDeathRestore(I18N.T("automation.reason.logged_out"));
            return;
        }

        var player = Svc.Objects.LocalPlayer;
        if (player == null || player.IsDead)
        {
            return;
        }

        var moved = System.Numerics.Vector3.Distance(player.Position, downedPosition);
        Svc.Log.Information(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[BOCCHI][復活] 距倒下={(Environment.TickCount64 - downedAt) / 1000.0:F1} 位移={moved:F1} 自動化={(downedWhileRunning ? "未恢復" : "倒地時沒在跑")}"));
        downedAt = 0;
        PromptDeathRestore();
    }

    private void GuardNavigation()
    {
        if (navigationGuardUntil == 0)
        {
            return;
        }

        // 讓 EurekaHelper 尋寶時非法模式還開著：讓位結束就收掉，免得停掉 BOCCHI 接著送的新路線。
        var released = guardForPotHunt ? automator?.automator.IsHoldingForPotHunt != true : IsAnyMovementRunning();
        if (Environment.TickCount64 > navigationGuardUntil || released || vnav == null)
        {
            navigationGuardUntil = 0;
            return;
        }

        try
        {
            var verdict = JudgeCurrentPath(guardDestinations, out var end);
            switch (verdict)
            {
                case OwnPathVerdict.Idle:
                    return;

                case OwnPathVerdict.Foreign:
                    // 別人送的移動會蓋掉 vnavmesh 裡 BOCCHI 還沒算完的請求，之後不會再有 BOCCHI 的路線開走。
                    navigationGuardUntil = 0;
                    Svc.Log.Information($"[BOCCHI][自動化] 停止後 vnavmesh 開始的移動不是 BOCCHI 送的（終點 {FormatPoint(end)}，"
                                        + $"離 BOCCHI 送過的目的地 {FormatDistance(guardDestinations.NearestXZ(end!.Value))} 米）：不停，交給送出它的插件或你自己。");
                    return;
            }

            vnav.Stop();
            if (!navigationGuardFired)
            {
                navigationGuardFired = true;
                var why = verdict == OwnPathVerdict.Own ? $"終點 {FormatPoint(end)} 是 BOCCHI 送過的目的地" : "讀不到路線終點，照舊停";
                Svc.Log.Information($"[BOCCHI][自動化] 停止後 vnavmesh 又開始移動（多半是停止前送出的尋路剛算完；{why}），已再停一次。");
            }
        }
        catch (Exception)
        {
            navigationGuardUntil = 0;
        }
    }

    private static string FormatDistance(float meters)
    {
        return float.IsFinite(meters) ? meters.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "?";
    }

    /// <summary>有沒有會自己移動的自動化在跑（非法模式、自動打怪、兩種巡迴）。</summary>
    public bool IsAnyMovementRunning()
    {
        foreach (var kind in AutomationKindExtensions.All)
        {
            if (kind.DrivesMovement() && IsRunning(kind))
            {
                return true;
            }
        }

        return false;
    }

    // 設定視窗的「啟用」勾選框直接改設定值、不經過這裡：取消勾選要補做收尾，勾選要照樣檢查能不能開始。
    private void WatchConfigWindowToggle()
    {
        var enabled = automator?.Config.Enabled == true;
        if (enabled == illegalSeenEnabled)
        {
            return;
        }

        illegalSeenEnabled = enabled;
        var source = I18N.T("automation.source.config_window");

        if (!enabled)
        {
            StopOneByUser(AutomationKind.IllegalMode, source, false, true);
            return;
        }

        if (!CanStartIgnoringRunning(AutomationKind.IllegalMode, out var whyNot))
        {
            automator!.Config.Enabled = false;
            illegalSeenEnabled = false;
            Svc.Log.Information($"[BOCCHI][自動化] 沒有開始「{GetName(AutomationKind.IllegalMode)}」（{source}）：{whyNot}");
            Svc.Chat.Print(I18N.T("automation.messages.cannot_start", new Dictionary<string, string> { ["name"] = GetName(AutomationKind.IllegalMode), ["reason"] = whyNot }));
            return;
        }

        Svc.Log.Information($"[BOCCHI][自動化] 開始「{GetName(AutomationKind.IllegalMode)}」（{source}）");
        InvalidateDeathRestoreOnManualStart(AutomationKind.IllegalMode);
        ApplyQuickRestartSkip(source);
    }

    private void OnTerritoryChanged(ushort territory)
    {
        var reason = I18N.T(territory == ZoneData.SOUTHHORN ? "automation.reason.zone_changed" : "automation.reason.left_zone");

        // 進島也停：新月島裡才能開始，所以從別處帶進來的只可能是殘留，不能讓它一進島就動起來。
        downedAt = 0;
        InvalidateDeathRestore(reason);
        if (IsAnyRunningOrWasRunning())
        {
            StopAll(reason, true, false, ActivityEnd.ZoneChanged);
        }

        InvalidateHuntProgress(HuntProgressInvalidation.ZoneChanged, reason);
        automator?.automator.ClearSkips(reason);
    }

    private void OnLogout(int type, int code)
    {
        var reason = I18N.T("automation.reason.logged_out");
        InvalidateDeathRestore(reason);
        if (IsAnyRunningOrWasRunning())
        {
            StopAll(reason, false, false, ActivityEnd.LoggedOut);
        }

        InvalidateHuntProgress(HuntProgressInvalidation.LoggedOut, reason);
        automator?.automator.ClearSkips(reason);
    }

    // 停止一律先於作廢：正在跑的先被 StopAll 轉成暫停，這裡再讓它失效。
    private void InvalidateHuntProgress(HuntProgressInvalidation why, string reason)
    {
        foreach (var kind in AutomationKindExtensions.All)
        {
            if (GetHunter(kind) is { } hunter && hunter.Progress.Invalidate(why))
            {
                Svc.Log.Information($"[BOCCHI][巡迴進度] 進度失效「{GetName(kind)}」（{reason}）：已跑 {hunter.ProgressText} 點；續跑停用，要再跑請按開始從頭跑");
            }
        }
    }

    private static void LogAbandon(AutomationKind kind, Hunter hunter, string reason)
    {
        Svc.Log.Information($"[BOCCHI][巡迴進度] 放棄進度「{GetName(kind)}」（{reason}）：已跑 {hunter.ProgressText} 點");
    }

    private void LogKept(AutomationKind kind, string reason, bool announce)
    {
        if (GetHunter(kind) is not { Progress.State: HuntProgressState.Paused } hunter)
        {
            return;
        }

        Svc.Log.Information($"[BOCCHI][巡迴進度] 保留進度「{GetName(kind)}」（{reason}）：已跑 {hunter.ProgressText} 點；不會自己續跑，要接著跑請按續跑或 /bocchi resume {kind.CommandKey()}");
        if (announce)
        {
            Svc.Chat.Print(I18N.T("automation.messages.progress_kept", new Dictionary<string, string> { ["name"] = GetName(kind), ["progress"] = hunter.ProgressText, ["key"] = kind.CommandKey() }));
        }
    }
}
