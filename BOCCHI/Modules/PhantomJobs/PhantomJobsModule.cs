using BOCCHI.Modules.StateManager;
using Ocelot.Modules;
using Ocelot.Windows;

namespace BOCCHI.Modules.PhantomJobs;

/// <summary>輔助職業：13 個輔助職業的等級總覽（純顯示）與練等模式（開始／停止經過 AutomationControl）。</summary>
[OcelotModule(1008, 8)]
public sealed class PhantomJobsModule(Plugin plugin, Config config) : Module(plugin, config)
{
    public override PhantomJobsConfig Config
    {
        get => PluginConfig.PhantomJobsConfig;
    }

    public readonly PhantomJobLeveling Leveling = new();

    private readonly Panel panel = new();

    private StateManagerModule? states;

    private bool statesResolved;

    public override void Update(UpdateContext context)
    {
        if (!Leveling.IsRunning)
        {
            return;
        }

        if (!statesResolved)
        {
            statesResolved = true;
            TryGetModule(out states);
        }

        Leveling.Tick(states);
    }

    public override bool RenderMainUi(RenderContext context)
    {
        if (!Config.ShowLevelOverview)
        {
            return false;
        }

        panel.Draw(this);
        return true;
    }
}
