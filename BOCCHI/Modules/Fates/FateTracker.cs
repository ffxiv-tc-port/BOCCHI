using System;
using System.Collections.Generic;
using System.Linq;
using BOCCHI.Data;
using ECommons.DalamudServices;
using Ocelot.Modules;

namespace BOCCHI.Modules.Fates;

public class FateTracker
{
    public readonly Dictionary<uint, Fate> Fates = [];

    public event Action<Fate>? OnFateSpawned;

    public event Action<Fate>? OnFateDespawned;

    // 同一個未知 FATE id 只記一次,避免每幀灌爆 log。
    private readonly HashSet<uint> reportedUnknownFates = [];

    public void Update(UpdateContext context)
    {
        var currentFates = Svc.Fates.ToDictionary(f => (uint)f.FateId, f => f);

        foreach (var (id, data) in currentFates)
        {
            // 🔴 Fate 的建構式會做 EventData.Fates[fate.FateId] —— 那是字典索引子,
            // 對照表裡沒有的 id 會擲 KeyNotFoundException。Svc.Fates 回的是當前區域「全部」的
            // FATE,而 EventData.Fates 只收錄新月島(南方奧內斯)那 13 個有魔晶石掉落的 FATE
            // (1962~1972、1976、1977;中間的 1973~1975 在資料表裡是同一批的保留列)。
            // 只要該區域出現任何一個不在表上的 FATE,這裡就會在模組的每幀 Update 迴圈裡擲例外。
            // 下游每一個消費端(Panel/Alerter/Automator/各指令)都直接讀 fate.Data.Demiatma 之類的欄位,
            // 也就是說「不在表上的 FATE」本來就不是這個外掛處理得了的東西 —— 略過才是正確語意。
            if (!EventData.Fates.ContainsKey(id))
            {
                if (reportedUnknownFates.Add(id))
                {
                    Svc.Log.Info($"[BOCCHI] FATE {id} 不在 EventData.Fates 對照表裡,已略過。(同一個 id 只記這一次)");
                }

                continue;
            }

            var fate = new Fate(data);
            if (!Fates.ContainsKey(id))
            {
                OnFateSpawned?.Invoke(fate);
            }

            Fates[id] = fate;
        }

        var despawned = Fates.Keys.Except(currentFates.Keys).ToList();
        foreach (var id in despawned)
        {
            OnFateDespawned?.Invoke(Fates[id]);
            Fates.Remove(id);
        }

        foreach (var fate in Fates.Values)
        {
            fate.Update(context);
        }
    }
}
