using System;
using System.Collections.Generic;
using BOCCHI.Data;
using ECommons.DalamudServices;
using Ocelot.Modules;
using FateContext = FFXIVClientStructs.FFXIV.Client.Game.Fate.FateContext;

namespace BOCCHI.Modules.Fates;

public class FateTracker
{
    public readonly Dictionary<uint, Fate> Fates = [];

    public event Action<Fate>? OnFateSpawned;

    public event Action<Fate>? OnFateDespawned;

    // 同一個未知 FATE id 只記一次,避免每幀灌爆 log。
    private readonly HashSet<uint> reportedUnknownFates = [];

    // 每幀重用，不另配置：本幀表上的位址（依表的順序）與 id、要移除的 id。
    private readonly List<nint> currentAddresses = [];

    private readonly HashSet<uint> currentIds = [];

    private readonly List<uint> despawned = [];

    /// <summary>
    /// 回 false＝這一幀的 F.A.T.E. 表讀不到：表不是空的而 PlayerState 還沒載入、有位址為 0 的格、或 id 重複。
    /// 那時追蹤的內容一筆都不動、事件不發：整張表先讀完、確定讀得到，才開始改狀態。
    /// 解參條件與 Dalamud FateTable.CreateFateReference 相同：位址非 0、而且 PlayerState 已載入。
    /// </summary>
    public unsafe bool Update(UpdateContext context)
    {
        var table = Svc.Fates;
        var count = table.Length;
        if (count > 0 && !Svc.PlayerState.IsLoaded)
        {
            return false;
        }

        currentAddresses.Clear();
        currentIds.Clear();
        for (var i = 0; i < count; i++)
        {
            var address = table.GetFateAddress(i);
            if (address == nint.Zero || !currentIds.Add(((FateContext*)address)->FateId))
            {
                return false;
            }

            currentAddresses.Add(address);
        }

        foreach (var address in currentAddresses)
        {
            var data = (FateContext*)address;
            uint id = data->FateId;

            // Fate 的建構式做 EventData.Fates[id]（字典索引子），對照表裡沒有的 id 會擲 KeyNotFoundException。
            // Svc.Fates 回的是當前區域全部的 FATE，EventData.Fates 只收錄新月島有魔晶石掉落的那幾個；
            // 下游每個消費端都直接讀 fate.Data 的欄位，不在表上的 FATE 略過才是正確語意。
            if (!EventData.Fates.ContainsKey(id))
            {
                if (reportedUnknownFates.Add(id))
                {
                    Svc.Log.Info($"[BOCCHI] FATE {id} 不在 EventData.Fates 對照表裡,已略過。(同一個 id 只記這一次)");
                }

                continue;
            }

            // 已經在追蹤的 FATE 一律沿用同一個 Fate 物件、只更新快照值：進度樣本序列（Fate.Progress）是物件的欄位，
            // 每幀重建的話樣本數永遠停在 1，EstimateTimeToCompletion 要兩筆以上才算得出預計完成時間。
            // Refresh 只在本幀使用剛讀到的位址，不保存。
            if (Fates.TryGetValue(id, out var fate))
            {
                fate.Refresh(data);
                continue;
            }

            fate = new Fate(data);
            OnFateSpawned?.Invoke(fate);
            Fates[id] = fate;
        }

        despawned.Clear();
        foreach (var id in Fates.Keys)
        {
            if (!currentIds.Contains(id))
            {
                despawned.Add(id);
            }
        }

        foreach (var id in despawned)
        {
            OnFateDespawned?.Invoke(Fates[id]);
            Fates.Remove(id);
        }

        foreach (var fate in Fates.Values)
        {
            fate.Update(context);
        }

        return true;
    }
}
