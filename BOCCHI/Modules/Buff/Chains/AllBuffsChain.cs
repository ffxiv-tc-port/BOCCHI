using BOCCHI.Data;
using ECommons.Automation.NeoTaskManager;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;

namespace BOCCHI.Modules.Buff.Chains;

public class AllBuffsChain(BuffModule module) : ChainFactory
{
    private readonly Job? StartingJob = Job.Current;

    protected override Chain Create(Chain chain)
    {
        // 不知道目前的輔助職業就整段不做：換了之後換不回來。
        chain.BreakIf(() => StartingJob == null);
        chain
            .Then(new KnightBuffChain(module))
            .Then(new MonkBuffChain(module))
            .Then(new BardBuffChain(module))
            .Then(() => StartingJob!.ChangeToChain());

        return chain;
    }

    public override TaskManagerConfiguration Config()
    {
        return new TaskManagerConfiguration { TimeLimitMS = 60000 };
    }
}
