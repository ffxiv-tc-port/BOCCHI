using System;
using System.Collections.Generic;
using System.Globalization;
using BOCCHI.Data;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI;
using Ocelot;

namespace BOCCHI.Modules.Fates;

/// <summary>
/// 魔法甕「快出現」（推估出現前 N 分鐘）與「出現了」的提醒：聊天視窗一則訊息＋提示音，各寫一行 Information。
/// 只在新月島、framework 執行緒（<see cref="FatesModule" /> 更新之後）；與非法模式開不開無關。不念塔塔露。
/// </summary>
public sealed class PotAlerter : IDisposable
{
    // 與 F.A.T.E. 出現訊息同一個標籤與顏色。
    private const ushort ChatTagColor = 45;

    private readonly FatesModule module;

    private readonly PotReminder reminder = new();

    public PotAlerter(FatesModule module)
    {
        this.module = module;
        module.potCycle.OnPotSpawned += OnPotSpawned;
        module.potCycle.OnCycleReset += reminder.Reset;
    }

    public void Tick()
    {
        var config = module.Config;
        if (!config.AlertPotSoon || Svc.ClientState.TerritoryType != ZoneData.SOUTHHORN)
        {
            return;
        }

        var pots = module.potCycle;
        var now = DateTime.UtcNow;
        if (!reminder.TakeSoon(pots.Cycle, now, config.GetPotCyclePeriod(), config.PotSoonMinutes, out var nextId, out var remaining))
        {
            return;
        }

        var name = pots.GetName(nextId);
        var minutes = ((int)Math.Ceiling(remaining.TotalMinutes)).ToString(CultureInfo.InvariantCulture);
        var at = (now + remaining).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        Print("modules.fates.messages.pot_soon", new Dictionary<string, string> { ["name"] = name, ["minutes"] = minutes, ["time"] = at });
        var sound = PlaySound(config);
        Svc.Log.Information($"[BOCCHI][魔法甕提醒] 快出現：「{name}」推估 {at} 出現（剩 {PotCycleTracker.FormatDuration(remaining)}；設定前 {config.PotSoonMinutes} 分鐘提醒）；{sound}");
    }

    private void OnPotSpawned(uint fateId, string name)
    {
        try
        {
            var config = module.Config;
            if (!config.AlertPotSpawned || Svc.ClientState.TerritoryType != ZoneData.SOUTHHORN || !reminder.TakeSpawn(fateId, DateTime.UtcNow))
            {
                return;
            }

            Print("modules.fates.messages.pot_spawned", new Dictionary<string, string> { ["name"] = name });
            var sound = PlaySound(config);
            Svc.Log.Information($"[BOCCHI][魔法甕提醒] 出現了：「{name}」；{sound}");
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[BOCCHI][魔法甕提醒] 魔法甕 {fateId} 出現的提醒失敗，略過（{ex.GetType().Name}）：{ex.Message}");
        }
    }

    private static void Print(string key, Dictionary<string, string> values)
    {
        Svc.Chat.Print(I18N.T(key, values), I18N.T("modules.fates.messages.tag"), ChatTagColor);
    }

    private static string PlaySound(FatesConfig config)
    {
        var sound = (uint)Math.Clamp(config.PotAlertSound, 1, 16);
        UIGlobals.PlayChatSoundEffect(sound);
        return $"提示音 se.{sound}";
    }

    public void Dispose()
    {
        module.potCycle.OnPotSpawned -= OnPotSpawned;
        module.potCycle.OnCycleReset -= reminder.Reset;
    }
}
