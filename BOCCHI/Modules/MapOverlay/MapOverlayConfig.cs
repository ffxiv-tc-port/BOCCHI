using Ocelot.Config.Attributes;
using Ocelot.Modules;

namespace BOCCHI.Modules.MapOverlay;

public class MapOverlayConfig : ModuleConfig
{
    /// <summary>推寶箱巡迴的規劃順序、生成點、幸運胡蘿蔔、目前目的地與等魔法甕的點給 Mappy；false＝不呼叫 Mappy。</summary>
    [Checkbox]
    public bool ShowOnMappy { get; set; } = true;

    [Checkbox]
    [DependsOn(nameof(ShowOnMappy))]
    [Indent]
    public bool ShowVisitedSpawnPoints { get; set; } = true;
}
