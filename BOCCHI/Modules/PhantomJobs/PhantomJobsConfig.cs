using Ocelot.Config.Attributes;
using Ocelot.Modules;

namespace BOCCHI.Modules.PhantomJobs;

public class PhantomJobsConfig : ModuleConfig
{
    [Checkbox] public bool ShowLevelOverview { get; set; } = false;
}
