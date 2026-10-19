using BOCCHI.ActionHelpers;
using BOCCHI.Data;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;

namespace BOCCHI.Modules.MobFarmer.Chains;

public class BattleBellChain(MobFarmerModule module) : ChainFactory
{
    private readonly Job? StartingJob = Job.Current;

    protected override Chain Create(Chain chain)
    {
        chain.BreakIf(() => Actions.Geomancer.BattleBell.GetRecastTime() >= module.Config.MaximumBattleBellWaitTime);
        // 不知道目前的輔助職業就不換職：換了之後換不回來。
        chain.BreakIf(() => StartingJob == null);

        chain.Then(Job.Geomancer.ChangeToChain);
        chain.Then(Actions.Geomancer.BattleBell.GetCastChain()).Wait(1000);
        chain.Then(() => StartingJob!.ChangeToChain());

        return chain;
    }
}
