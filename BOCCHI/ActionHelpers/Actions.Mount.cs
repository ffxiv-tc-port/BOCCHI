using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BOCCHI.ActionHelpers;

public static partial class Actions
{
    public static Action MountRoulette { get; private set; } = new(ActionType.GeneralAction, 9);

    public static Action Mount(uint id)
    {
        return new Action(ActionType.Mount, id);
    }

    public static Action Unmount { get; private set; } = new(ActionType.Mount, 0);

    /// <summary>一般動作「跳下」（從坐騎上下來）。</summary>
    public static Action Dismount { get; private set; } = new(ActionType.GeneralAction, 23);

    public static void TryUnmount()
    {
        if (Svc.Condition[ConditionFlag.Mounted])
        {
            Unmount.Cast();
        }
    }
}
