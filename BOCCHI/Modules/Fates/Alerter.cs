using System;
using System.Collections.Generic;
using BOCCHI.Enums;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI;
using Ocelot;

namespace BOCCHI.Modules.Fates;

public class Alerter : IDisposable
{
    private readonly FatesModule module;

    // 聊天訊息前的標籤顏色（UIColor 列號）：綠色，與緊急遭遇戰的橘色區分。
    private const ushort ChatTagColor = 45;

    private Dictionary<Demiatma, Func<bool>> DemiatmaAlerts
    {
        get => new()
        {
            [Demiatma.Azurite] = () => module.Config.AlertAzurite,
            [Demiatma.Verdigris] = () => module.Config.AlertVerdigris,
            [Demiatma.Malachite] = () => module.Config.AlertMalachite,
            [Demiatma.Realgar] = () => module.Config.AlertRealgar,
            [Demiatma.CaputMortuum] = () => module.Config.AlertCaputMortuum,
            [Demiatma.Orpiment] = () => module.Config.AlertOrpiment,
        };
    }

    public Alerter(FatesModule module)
    {
        this.module = module;

        this.module.tracker.OnFateSpawned += OnFateSpawned;
        this.module.tracker.OnFateDespawned += OnFateDespawned;
    }

    /// <summary>魔法甕出現的提醒（<see cref="PotAlerter"/>）開著時，這裡的出現訊息與提示音跳過魔法甕；消失照舊。</summary>
    public static bool LeavesSpawnToPotAlert(FatesConfig config, uint fateId)
    {
        return config.AlertPotSpawned && PotCycle.IsPot(fateId);
    }

    private void OnFateSpawned(Fate fate)
    {
        if (LeavesSpawnToPotAlert(module.Config, fate.Id))
        {
            return;
        }

        if (module.Config.LogSpawn)
        {
            Svc.Chat.Print(I18N.T("modules.fates.messages.spawned", new Dictionary<string, string> { ["name"] = fate.Name }), I18N.T("modules.fates.messages.tag"), ChatTagColor);
        }

        if (!ShouldAlertForFate(fate))
        {
            return;
        }

        UIGlobals.PlaySoundEffect(66);
    }

    private void OnFateDespawned(Fate fate)
    {
        if (module.Config.LogSpawn)
        {
            Svc.Chat.Print(I18N.T("modules.fates.messages.despawned", new Dictionary<string, string> { ["name"] = fate.Name }), I18N.T("modules.fates.messages.tag"), ChatTagColor);
        }

        if (!ShouldAlertForFate(fate))
        {
            return;
        }

        UIGlobals.PlaySoundEffect(68);
    }

    private bool ShouldAlertForFate(Fate fate)
    {
        if (module.Config.AlertAll)
        {
            return true;
        }

        if (fate.Data.Demiatma != null)
        {
            var demiatma = (Demiatma)fate.Data.Demiatma;
            if (DemiatmaAlerts.TryGetValue(demiatma, out var getter))
            {
                return getter();
            }
        }

        return false;
    }

    public void Dispose()
    {
        module.tracker.OnFateSpawned -= OnFateSpawned;
        module.tracker.OnFateDespawned -= OnFateDespawned;
    }
}
