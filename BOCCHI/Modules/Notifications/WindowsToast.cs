using System;
using System.Linq;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using ECommons.DalamudServices;

namespace BOCCHI.Modules.Notifications;

/// <summary>請 NotificationMaster 顯示 Windows 系統匣通知；只在 framework 執行緒呼叫，絕不往外擲。</summary>
public static class WindowsToast
{
    public enum Result
    {
        /// <summary>出錯了（例外寫進 log）。零值刻意給這個。</summary>
        Failed = 0,

        Sent,

        /// <summary>NotificationMaster 收到了但回 false（它自己出錯）。</summary>
        Rejected,

        /// <summary>NotificationMaster 沒有安裝或沒有啟用。</summary>
        NotLoaded,
    }

    private const string Endpoint = "NotificationMasterAPI.DisplayToastNotification";

    private const string InternalName = "NotificationMaster";

    // 簽章逐字對應 NotificationMaster 的 DisplayToastNotification(string requesterPlugin, string title, string text) -> bool。
    private static ICallGateSubscriber<string, string, string, bool>? display;

    public static Result Show(string title, string text)
    {
        try
        {
            display ??= Svc.PluginInterface.GetIpcSubscriber<string, string, string, bool>(Endpoint);
            return display.InvokeFunc("BOCCHI", title, text) ? Result.Sent : Result.Rejected;
        }
        catch (IpcNotReadyError)
        {
            return IsLoaded() ? Result.Failed : Result.NotLoaded;
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[BOCCHI][通知] 呼叫 {Endpoint} 失敗（{ex.GetType().Name}）：{ex.Message}");
            return Result.Failed;
        }
    }

    private static bool IsLoaded()
    {
        try
        {
            return Svc.PluginInterface.InstalledPlugins.Any(p => p.InternalName == InternalName && p.IsLoaded);
        }
        catch (Exception)
        {
            return true;
        }
    }
}
