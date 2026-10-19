using System;
using System.Collections.Generic;
using BOCCHI.Data;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.Fates;
using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using Ocelot;
using Ocelot.Modules;
using Ocelot.Windows;

namespace BOCCHI.Modules.Notifications;

/// <summary>
/// 緊急遭遇戰開放報名、魔法甕出現時，透過 NotificationMaster 發 Windows 通知。純通知：不做任何遊戲內操作。
/// 事件都在 framework 執行緒觸發，處理器自己吞例外（例外不可以打斷觸發端那一幀的更新）。
/// 每一場只在出現那一刻通知一次；遊戲在前景時預設不發。
/// </summary>
[OcelotModule(1007)]
public class NotificationsModule(Plugin plugin, Config config) : Module(plugin, config)
{
    public override NotificationsConfig Config
    {
        get => PluginConfig.NotificationsConfig;
    }

    public override bool ShouldInitialize
    {
        get => true;
    }

    public override bool ShouldUpdate
    {
        get => false;
    }

    // 同一場緊急遭遇戰只通知一次：鍵＝(id, 開始時間)。進島時清空。
    private readonly HashSet<(ushort, int)> notifiedCriticalEncounters = [];

    // 同一個魔法甕這段時間內不再通知：F.A.T.E. 清單短暫少掉再回來時不會再響一次。
    private static readonly TimeSpan PotRenotifyFloor = TimeSpan.FromMinutes(10);

    private readonly Dictionary<uint, DateTime> lastPotNotified = [];

    private CriticalEncounterTracker? criticalEncounters;

    private PotCycleTracker? pots;

    private WindowsToast.Result? testResult;

    public override void PostInitialize()
    {
        if (TryGetModule<CriticalEncountersModule>(out var ce) && ce != null)
        {
            criticalEncounters = ce.Tracker;
            criticalEncounters.OnRegisterState += OnCriticalEncounterRegister;
        }

        if (TryGetModule<FatesModule>(out var fates) && fates != null)
        {
            pots = fates.potCycle;
            pots.OnPotSpawned += OnPotSpawned;
        }

        Svc.ClientState.TerritoryChanged += OnZoneChanged;
    }

    private void OnCriticalEncounterRegister(CriticalEncounterSnapshot ev)
    {
        try
        {
            if (!Config.NotifyCriticalEncounters || !notifiedCriticalEncounters.Add((ev.DynamicEventId, ev.StartTimestamp)))
            {
                return;
            }

            var startsIn = DateTimeOffset.FromUnixTimeSeconds(ev.StartTimestamp) - DateTimeOffset.UtcNow;
            var body = ev.StartTimestamp > 0 && startsIn > TimeSpan.Zero
                ? I18N.T("modules.notifications.toast.ce_body_with_time", new Dictionary<string, string>
                {
                    ["name"] = ev.Name,
                    ["time"] = $"{(int)startsIn.TotalMinutes}:{startsIn.Seconds:D2}",
                })
                : I18N.T("modules.notifications.toast.ce_body", new Dictionary<string, string> { ["name"] = ev.Name });

            Send(T("toast.ce_title"), body, $"緊急遭遇戰「{ev.Name}」開放報名");
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[BOCCHI][通知] 處理緊急遭遇戰 {ev.DynamicEventId} 的通知失敗，略過（{ex.GetType().Name}）：{ex.Message}");
        }
    }

    private void OnPotSpawned(uint fateId, string name)
    {
        try
        {
            if (!Config.NotifyPotFates)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (lastPotNotified.TryGetValue(fateId, out var last) && now - last < PotRenotifyFloor)
            {
                Svc.Log.Information($"[BOCCHI][通知] 魔法甕「{name}」{PotRenotifyFloor.TotalMinutes:0} 分鐘內已通知過，這次不再通知");
                return;
            }

            lastPotNotified[fateId] = now;
            Send(T("toast.pot_title"), I18N.T("modules.notifications.toast.pot_body", new Dictionary<string, string> { ["name"] = name }), $"魔法甕「{name}」出現");
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[BOCCHI][通知] 處理魔法甕 {fateId} 的通知失敗，略過（{ex.GetType().Name}）：{ex.Message}");
        }
    }

    private void Send(string title, string body, string what)
    {
        if (!Config.NotifyWhenGameFocused && Dalamud.Utility.Util.ApplicationIsActivated())
        {
            Svc.Log.Information($"[BOCCHI][通知] {what}：遊戲視窗在前景，依設定不發 Windows 通知");
            return;
        }

        var result = WindowsToast.Show(title, body);
        Svc.Log.Information($"[BOCCHI][通知] {what}：{Describe(result)}");
    }

    private static string Describe(WindowsToast.Result result)
    {
        return result switch
        {
            WindowsToast.Result.Sent => "已送出 Windows 通知",
            WindowsToast.Result.Rejected => "NotificationMaster 收到但沒有顯示（它自己出錯）",
            WindowsToast.Result.NotLoaded => "NotificationMaster 沒有安裝或沒有啟用，沒有送出",
            _ => "呼叫 NotificationMaster 出錯，沒有送出",
        };
    }

    public override void RenderConfigUi(RenderContext context)
    {
        base.RenderConfigUi(context);

        // 手動測試：按下才送一則，不看前景設定（按的當下遊戲必定在前景）。
        ImGui.Indent(16);
        if (ImGui.Button(T("config.test.label")))
        {
            testResult = WindowsToast.Show(T("toast.test_title"), T("toast.test_body"));
            Svc.Log.Information($"[BOCCHI][通知] 測試通知：{Describe(testResult.Value)}");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(T("config.test.tooltip"));
        }

        if (testResult is { } result)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(result switch
            {
                WindowsToast.Result.Sent => T("config.test.sent"),
                WindowsToast.Result.NotLoaded => T("config.test.not_loaded"),
                _ => T("config.test.failed"),
            });
        }

        ImGui.Unindent(16);
    }

    private void OnZoneChanged(ushort territory)
    {
        if (territory == ZoneData.SOUTHHORN)
        {
            notifiedCriticalEncounters.Clear();
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        Svc.ClientState.TerritoryChanged -= OnZoneChanged;
        if (criticalEncounters != null)
        {
            criticalEncounters.OnRegisterState -= OnCriticalEncounterRegister;
        }

        if (pots != null)
        {
            pots.OnPotSpawned -= OnPotSpawned;
        }
    }
}
