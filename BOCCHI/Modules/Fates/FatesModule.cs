using System.Collections.Generic;
using Ocelot.Modules;
using Ocelot.Windows;

namespace BOCCHI.Modules.Fates;

[OcelotModule(1001, 5)]
public class FatesModule : Module
{
    public override FatesConfig Config
    {
        get => PluginConfig.FatesConfig;
    }

    public override bool IsEnabled
    {
        get => Config.IsPropertyEnabled(nameof(Config.Enabled));
    }

    public override bool ShouldUpdate
    {
        get => true;
    }

    public readonly FateTracker tracker = new();

    public readonly PotCycleTracker potCycle = new();

    public Dictionary<uint, Fate> fates
    {
        get => tracker.Fates;
    }

    private readonly Panel panel = new();

    private readonly Alerter alerter;

    private readonly PotAlerter potAlerter;

    public FatesModule(Plugin plugin, Config config)
        : base(plugin, config)
    {
        alerter = new Alerter(this);
        potAlerter = new PotAlerter(this);
    }

    public override void Update(UpdateContext context)
    {
        // 表讀不到的那一幀，魔法甕週期也不拿沒更新的追蹤內容去算。
        if (tracker.Update(context))
        {
            potCycle.Update(tracker, Config.GetPotCyclePeriod());
            potAlerter.Tick();
        }
    }

    public override bool RenderMainUi(RenderContext context)
    {
        panel.Draw(this);
        return true;
    }

    public override void OnTerritoryChanged(ushort id)
    {
        fates.Clear();
    }

    public override void Dispose()
    {
        base.Dispose();
        alerter.Dispose();
        potAlerter.Dispose();
        potCycle.Dispose();
    }
}
