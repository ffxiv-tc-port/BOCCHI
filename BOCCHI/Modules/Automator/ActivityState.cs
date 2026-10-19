using Ocelot;

namespace BOCCHI.Modules.Automator;

public enum ActivityState
{
    Idle,
    Pathfinding,
    WaitingToStartCriticalEncounter,
    Participating,
    Done,

    /// <summary>魔法甕快出現時在出生點附近等它出現（<see cref="PotWaitActivity" />）。</summary>
    WaitingForPot,
}

public static class ActivityStateExtensions
{
    public static string ToLabel(this ActivityState state)
    {
        return I18N.T(state switch
        {
            ActivityState.Idle => "modules.automator.panel.activity_state.idle",
            ActivityState.Pathfinding => "modules.automator.panel.activity_state.pathfinding",
            ActivityState.WaitingToStartCriticalEncounter => "modules.automator.panel.activity_state.waiting_to_start",
            ActivityState.Participating => "modules.automator.panel.activity_state.participating",
            ActivityState.Done => "modules.automator.panel.activity_state.done",
            ActivityState.WaitingForPot => "modules.automator.panel.activity_state.waiting_for_pot",
            _ => "modules.automator.panel.activity_state.unknown",
        });
    }
}
