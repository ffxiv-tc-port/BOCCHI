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

            // 🔴 已經在追蹤的 FATE 一律沿用同一個 Fate 物件,只更新它的快照值。
            // 原本這裡每幀都 `new Fate(data)` 再覆蓋回字典,而進度樣本序列(Fate.Progress)是
            // 那個物件的欄位 —— 於是每幀開局都是空序列,`Fate.Update` 補進一筆之後又被下一幀
            // 的新物件丟掉,樣本數永遠停在 1。`EventProgress.EstimateTimeToCompletion` 在
            // `samples.Count < 2` 時回 null,所以 Panel 的「預計完成時間」從來沒有顯示過。
            // 物件跨幀重用之後樣本才累積得起來。
            // (Refresh 只在本幀使用剛取得的 IFate,不保存指標 —— 見 Fate 類別的註解。)
            if (Fates.TryGetValue(id, out var fate))
            {
                fate.Refresh(data);
                continue;
            }

            fate = new Fate(data);
            OnFateSpawned?.Invoke(fate);
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
