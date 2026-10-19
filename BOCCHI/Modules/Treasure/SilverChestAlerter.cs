using System;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.Notifications;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace BOCCHI.Modules.Treasure;

/// <summary>
/// 新月島上看到新的白銀寶箱時響一次提示音：同一個寶箱只響一次，離開新月島或換區就重新計算。
/// 另可請塔塔露誇一句（「新月島事件叫塔塔露誇獎」）：與提示音各記各的，兩邊互不影響。
/// 只在框架執行緒、TreasureTracker 更新之後呼叫。
/// </summary>
public sealed class SilverChestAlerter
{
    private const int Capacity = 256;

    private readonly SilverChestMemory alerted = new(Capacity);

    private readonly SilverChestMemory praised = new(Capacity);

    public void Tick(TreasureModule module, bool praise)
    {
        // 白銀寶箱的模型在別的副本也有用到，只在新月島響。
        if (!ZoneData.IsInOccultCrescent())
        {
            alerted.Clear();
            praised.Clear();
            return;
        }

        var config = module.Config;
        var alert = config.ShouldAlertSilverChests;
        if (!alert && !praise)
        {
            return;
        }

        var played = false;
        var spoke = false;
        foreach (var treasure in module.Treasures)
        {
            if (treasure.GetTreasureType() != TreasureType.Silver)
            {
                continue;
            }

            var position = treasure.LastKnownPosition;
            if (praise && praised.Add(treasure.Id, position) && !spoke)
            {
                spoke = true;
                TataruPraiseIpc.Praise(TataruPraiseIpc.SilverChestFound, $"發現白銀寶箱（id {treasure.Id:X}）");
            }

            if (!alert || !alerted.Add(treasure.Id, position))
            {
                continue;
            }

            // 同一幀出現好幾個（例如剛進島）只響一聲，每個仍各寫一行。
            var sound = (uint)Math.Clamp(config.SilverChestAlertSound, 1, 16);
            var note = played ? "同一幀已響過" : $"提示音 se.{sound}";
            if (!played)
            {
                UIGlobals.PlayChatSoundEffect(sound);
                played = true;
            }

            Svc.Log.Information($"[BOCCHI][銀寶箱] 發現白銀寶箱：位置 ({position.X:F1}, {position.Y:F1}, {position.Z:F1})，距離 {Player.DistanceTo(position):F1} 米，id {treasure.Id:X}，{note}");
        }
    }

    public void Reset()
    {
        alerted.Clear();
        praised.Clear();
    }
}
