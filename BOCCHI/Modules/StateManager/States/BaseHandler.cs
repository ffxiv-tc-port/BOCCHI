using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Ocelot.States;

namespace BOCCHI.Modules.StateManager.States;

public abstract class BaseHandler(StateManagerModule module) : StateHandler<State, StateManagerModule>(module)
{
    protected bool IsInCombat()
    {
        return Svc.Condition[ConditionFlag.InCombat];
    }

    protected unsafe bool IsInFate()
    {
        // 🔴 FateManager 是 [StaticAddress(..., isPointer: true)],Instance() 回傳的是靜態槽
        //    「裡面的值」,可以合法為 null(登入流程、區域切換期間)。直接 -> 解參考產生的
        //    AccessViolationException 在 .NET Core 是 corrupted-state exception,try/catch
        //    攔不到,會直接把遊戲帶走。查詢路徑 fail-closed:取不到就回 false(當作不在 FATE)。
        var fateManager = FateManager.Instance();
        return fateManager != null && fateManager->CurrentFate is not null;
    }

    protected unsafe bool IsInCriticalEncounter()
    {
        var dec = DynamicEventContainer.GetInstance();
        return dec != null && dec->CurrentEventId != 0;
    }
}
