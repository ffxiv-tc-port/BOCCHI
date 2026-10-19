using FFXIVClientStructs.FFXIV.Client.Game;

namespace BOCCHI.ActionHelpers;

public static partial class Actions
{
    public static Action Sprint { get; private set; } = new(ActionType.GeneralAction, 4);

    public static Action Return { get; private set; } = new(ActionType.GeneralAction, 8);

    /// <summary>新月島裡按「返回」實際詠唱的技能（走公共冷卻）；只拿來讀冷卻，施放一律用 <see cref="Return"/>。</summary>
    public static Action OccultReturn { get; private set; } = new(ActionType.Action, 41343);
}
