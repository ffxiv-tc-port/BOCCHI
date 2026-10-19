using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace BOCCHI.ItemHelpers;

public unsafe class Item(uint id)
{
    public int Count()
    {
        try
        {
            return InventoryManager.Instance()->GetInventoryItemCount(id);
        }
        catch
        {
            return 0;
        }
    }

    public void Use()
    {
        try
        {
            // AgentInventoryContext.Instance() 內部鏈 AgentModule -> UIModule -> Framework 任一層是 null 就回 null（登入前、切場景時是常態）。
            // 外圍的 try 只擋得住特徵碼失配時擲出的 InvalidOperationException；裸解參考 null 原生指標是 try/catch 攔不到的 AccessViolationException，所以事前判空。
            // fail-closed：agent 不在就不用道具（靜默，呼叫端本來就吞例外）。
            var agent = AgentInventoryContext.Instance();
            if (agent == null)
            {
                return;
            }

            agent->UseItem(id);
        }
        catch
        {
            // ignored
        }
    }
}
