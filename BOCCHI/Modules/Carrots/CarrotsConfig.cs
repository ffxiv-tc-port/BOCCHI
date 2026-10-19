using Ocelot.Config.Attributes;
using Ocelot.Modules;

namespace BOCCHI.Modules.Carrots;

public class CarrotsConfig : ModuleConfig
{
    [Checkbox]
    [Label("generic.label.enabled")]
    public bool Enabled { get; set; } = true;

    [Checkbox]
    [DependsOn(nameof(Enabled))]
    public bool DrawLineToCarrots { get; set; } = true;

    public bool ShouldDrawLineToCarrots
    {
        get => IsPropertyEnabled(nameof(DrawLineToCarrots));
    }

    [Checkbox]
    [DependsOn(nameof(Enabled))]
    public bool AlertCarrots { get; set; } = true;

    public bool ShouldAlertCarrots
    {
        get => IsPropertyEnabled(nameof(AlertCarrots));
    }

    /// <summary>提示音編號，1～16 與聊天的 se.1～se.16 相同。</summary>
    [IntRange(1, 16)]
    [DependsOn(nameof(Enabled), nameof(AlertCarrots))]
    [Indent]
    public int CarrotAlertSound { get; set; } = 1;

    /// <summary>另外要開「新月島事件叫塔塔露誇獎」（NotificationsConfig.PraiseOccultEvents）才會呼叫。</summary>
    [Checkbox]
    [DependsOn(nameof(Enabled))]
    public bool PraiseCarrots { get; set; } = true;

    public bool ShouldPraiseCarrots
    {
        get => IsPropertyEnabled(nameof(PraiseCarrots));
    }

    [Checkbox]
    [Experimental]
    [Illegal]
    [RequiredPlugin("vnavmesh", "Lifestream")]
    [DependsOn(nameof(Enabled))]
    [Label("modules.carrots.config.show_hunt_button.label")]
    public bool EnableCarrotHunt { get; set; } = false;

    public bool ShouldEnableCarrotHunt
    {
        get => IsPropertyEnabled(nameof(EnableCarrotHunt));
    }

    [Checkbox]
    [Experimental]
    [DependsOn(nameof(Enabled), nameof(EnableCarrotHunt))]

    public bool RepeatCarrotHunt { get; set; } = false;
}
