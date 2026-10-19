using System.Collections.Generic;
using System.Linq;
using BOCCHI.Data;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.GameFunctions;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace BOCCHI.Modules.MobFarmer;

public class Scanner(MobFarmerModule module)
{
    public IEnumerable<IBattleNpc> Mobs { get; private set; } = [];

    // Mobs 是不是接著 TargetHelper 的查詢（Tick 當下它已經更新過）；還沒有時 Mobs 是空的。
    private bool live;

    public IEnumerable<IBattleNpc> InCombat
    {
        get => Mobs.Where(o => o.IsTargetingPlayer());
    }

    public IEnumerable<IBattleNpc> NotInCombat
    {
        get => Mobs.Where(o => !o.HasTarget());
    }

    public void Tick(IFramework _)
    {
        Mobs = TargetHelper.Enemies.Where(IsFarmTarget);
        live = TargetHelper.HasScanned;
    }

    /// <summary>
    /// 同 NotInCombat.Count()／InCombat.Count()（條件逐項相同；數量與順序無關，所以不排序），
    /// 但只走訪物件表一次、不建查詢：主視窗每幀要這兩個數字。框架執行緒專用。
    /// </summary>
    public void CountEngagement(out int notInCombat, out int inCombat)
    {
        notInCombat = 0;
        inCombat = 0;
        if (!live)
        {
            return;
        }

        var playerAddress = Svc.Objects.LocalPlayer?.Address ?? nint.Zero;
        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc { IsDead: false, IsTargetable: true } npc || !npc.IsHostile() || !IsFarmTarget(npc))
            {
                continue;
            }

            var target = npc.TargetObject;
            if (target == null)
            {
                notInCombat++;
            }
            else if (target.Address == playerAddress)
            {
                inCombat++;
            }
        }
    }

    private unsafe bool IsFarmTarget(IBattleNpc o)
    {
        if (Player.DistanceTo(o) > module.Config.MaxEuclideanDistance)
        {
            return false;
        }

        var chara = (BattleChara*)o.Address;

        if (module.Config.Mobs.Contains((Mob)o.NameId))
        {
            return chara->ForayInfo.Level <= module.Config.MaxMobLevel;
        }

        if (!module.Config.ConsiderSpecialMobs)
        {
            return false;
        }

        if (MobData.MobsWithSpawnCondition.Contains((Mob)o.NameId))
        {
            return chara->ForayInfo.Level <= module.Config.MaxMobLevel;
        }

        return false;
    }
}
