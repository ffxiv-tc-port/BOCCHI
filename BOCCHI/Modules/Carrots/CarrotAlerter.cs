using System;
using BOCCHI.Data;
using BOCCHI.Modules.Notifications;
using BOCCHI.Modules.Treasure;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace BOCCHI.Modules.Carrots;

/// <summary>
/// 新月島上看到新的幸運胡蘿蔔時響一次提示音、請塔塔露念一句（做法同 <see cref="SilverChestAlerter"/>）：
/// 同一根（物件 id 相同、位置差 1 米內）只各一次，離開新月島就重新計算；提示音與塔塔露各記各的。
/// 只在框架執行緒、CarrotsTracker 更新之後呼叫。
/// </summary>
public sealed class CarrotAlerter
{
    private const int Capacity = 64;

    private readonly SilverChestMemory alerted = new(Capacity);

    private readonly SilverChestMemory praised = new(Capacity);

    /// <param name="praiseAllowed">「新月島事件叫塔塔露誇獎」總開關。</param>
    public void Tick(CarrotsModule module, bool praiseAllowed)
    {
        if (!ZoneData.IsInOccultCrescent())
        {
            alerted.Clear();
            praised.Clear();
            return;
        }

        var config = module.Config;
        var alert = config.ShouldAlertCarrots;
        var praise = praiseAllowed && config.ShouldPraiseCarrots;
        if (!alert && !praise)
        {
            return;
        }

        var played = false;
        var spoke = false;
        foreach (var carrot in module.carrots)
        {
            var position = carrot.LastKnownPosition;
            if (praise && praised.Add(carrot.Id, position) && !spoke)
            {
                spoke = true;
                TataruPraiseIpc.Praise(TataruPraiseIpc.CarrotFound, $"發現幸運胡蘿蔔（id {carrot.Id:X}）");
            }

            if (!alert || !alerted.Add(carrot.Id, position))
            {
                continue;
            }

            // 同一幀出現好幾根（例如剛進島）只響一聲，每根仍各寫一行。
            var sound = (uint)Math.Clamp(config.CarrotAlertSound, 1, 16);
            var note = played ? "同一幀已響過" : $"提示音 se.{sound}";
            if (!played)
            {
                UIGlobals.PlayChatSoundEffect(sound);
                played = true;
            }

            Svc.Log.Information($"[BOCCHI][胡蘿蔔] 發現幸運胡蘿蔔：位置 ({position.X:F1}, {position.Y:F1}, {position.Z:F1})，距離 {Player.DistanceTo(position):F1} 米，id {carrot.Id:X}，{note}");
        }
    }
}
