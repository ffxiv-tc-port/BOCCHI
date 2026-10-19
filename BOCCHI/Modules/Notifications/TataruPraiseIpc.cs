using System;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using ECommons.DalamudServices;

namespace BOCCHI.Modules.Notifications;

/// <summary>
/// 請「塔塔露誇獎」（TataruPraise）念一句：只用 <c>TataruPraise.Praise</c>（Func&lt;string, bool&gt;，回傳有沒有排進播放）。
/// 只在 framework 執行緒、事件發生那一次呼叫（不是每幀）。對方沒裝或沒載入時安靜跳過；
/// 情境鍵逐字對應 TataruPraise 的 PraiseCategory，對方不認得的鍵只會回 false。
/// </summary>
public static class TataruPraiseIpc
{
    public const string CriticalEncounterSuccess = "緊急遭遇戰成功";

    public const string KnowledgeLevelUp = "知見等級提升";

    public const string SilverChestFound = "發現白銀寶箱";

    public const string CarrotFound = "發現幸運胡蘿蔔";

    private static ICallGateSubscriber<string, bool>? praise;

    public static void Praise(string category, string what)
    {
        try
        {
            praise ??= Svc.PluginInterface.GetIpcSubscriber<string, bool>("TataruPraise.Praise");
            var accepted = praise.InvokeFunc(category);
            Svc.Log.Information(accepted
                ? $"[BOCCHI][塔塔露] {what}：Praise（{category}）回 true，已排進播放"
                : $"[BOCCHI][塔塔露] {what}：Praise（{category}）回 false（總開關關著、這個情境停用、冷卻中、還沒合成語音，或 TataruPraise 不認得這個情境）");
        }
        catch (IpcNotReadyError)
        {
            // 沒裝或沒載入是常態，不寫記錄。
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[BOCCHI][塔塔露] {what}：呼叫 TataruPraise.Praise 失敗（{ex.GetType().Name}）：{ex.Message}");
        }
    }
}
