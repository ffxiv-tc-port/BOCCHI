using Ocelot.Config.Attributes;
using Ocelot.Modules;

namespace BOCCHI.Modules.Aggro;

[Text("config.text")]
public class AggroConfig : ModuleConfig
{
    [Checkbox] public bool Enabled { get; set; } = false;

    [Checkbox]
    [DependsOn(nameof(Enabled))]
    public bool DrawSight { get; set; } = true;

    [FloatRange(3f, 30f)]
    [DependsOn(nameof(Enabled), nameof(DrawSight))]
    public float SightRadius { get; set; } = 9f;

    [IntRange(30, 180)]
    [DependsOn(nameof(Enabled), nameof(DrawSight))]
    public int SightAngle { get; set; } = 90;

    [Checkbox]
    [DependsOn(nameof(Enabled))]
    public bool DrawSound { get; set; } = true;

    [FloatRange(3f, 30f)]
    [DependsOn(nameof(Enabled), nameof(DrawSound))]
    public float SoundRadius { get; set; } = 9f;

    [Checkbox]
    [DependsOn(nameof(Enabled))]
    public bool DrawProximity { get; set; } = true;

    [FloatRange(3f, 30f)]
    [DependsOn(nameof(Enabled), nameof(DrawProximity))]
    public float ProximityRadius { get; set; } = 9f;

    [Checkbox]
    [DependsOn(nameof(Enabled))]
    public bool DrawMagic { get; set; } = true;

    [FloatRange(3f, 30f)]
    [DependsOn(nameof(Enabled), nameof(DrawMagic))]
    public float MagicRadius { get; set; } = 9f;

    [Checkbox]
    [DependsOn(nameof(Enabled))]
    public bool DrawBlood { get; set; } = true;

    [FloatRange(3f, 30f)]
    [DependsOn(nameof(Enabled), nameof(DrawBlood))]
    public float BloodRadius { get; set; } = 9f;

    [IntRange(5, 95)]
    [DependsOn(nameof(Enabled))]
    public int BloodHpThreshold { get; set; } = 50;

    [Checkbox]
    [DependsOn(nameof(Enabled))]
    public bool ShowUnknown { get; set; } = true;

    [FloatRange(3f, 30f)]
    [DependsOn(nameof(Enabled), nameof(ShowUnknown))]
    public float UnknownRadius { get; set; } = 9f;

    // 迴避不依賴「啟用」（顯示）：顯示關著也照樣避，用的是上面同一組估計距離。
    [Checkbox] public bool AvoidWhileMoving { get; set; } = true;

    [Checkbox]
    [DependsOn(nameof(AvoidWhileMoving))]
    public bool AvoidPossible { get; set; } = true;

    [FloatRange(0f, 10f)]
    [DependsOn(nameof(AvoidWhileMoving))]
    public float AvoidMargin { get; set; } = 2.5f;

    // 舊設定檔沒有這個鍵時沿用初始值（Newtonsoft 只覆寫檔裡有的鍵）＝既有使用者也是開。
    [Checkbox]
    [DependsOn(nameof(AvoidWhileMoving))]
    public bool WalkThroughSound { get; set; } = true;
}
