using System.Collections.Generic;
using Ocelot.Modules;
using Ocelot.Windows;

namespace BOCCHI.Modules.Carrots;

[OcelotModule(1004, 2)]
public class CarrotsModule(Plugin plugin, Config config) : Module(plugin, config)
{
    public override CarrotsConfig Config
    {
        get => PluginConfig.CarrotsConfig;
    }

    public override bool ShouldUpdate
    {
        get => true;
    }

    public override bool ShouldInitialize
    {
        get => true;
    }

    public override bool IsEnabled
    {
        get => Config.IsPropertyEnabled(nameof(Config.Enabled));
    }

    private readonly CarrotsTracker tracker = new();

    private readonly CarrotAlerter alerter = new();

    private CarrotHunt hunter = null!;

    /// <summary>PostInitialize 之前（或它失敗時）是 null。</summary>
    public CarrotHunt? Hunter
    {
        get => hunter;
    }

    public List<Carrot> carrots
    {
        get => tracker.carrots;
    }

    private readonly Panel panel = new();

    private readonly Radar radar = new();

    public override void PostInitialize()
    {
        hunter = new CarrotHunt(this);
    }

    public override void Update(UpdateContext context)
    {
        tracker.Tick(context.Framework);
        alerter.Tick(this, PluginConfig.NotificationsConfig.PraiseOccultEvents);
        hunter.Update();
    }

    public override void Render(RenderContext context)
    {
        radar.Draw(context, this);
    }

    public override bool RenderMainUi(RenderContext context)
    {
        panel.Draw(this);

        if (Config.ShouldEnableCarrotHunt)
        {
            hunter.Draw(this);
        }

        return true;
    }
}
