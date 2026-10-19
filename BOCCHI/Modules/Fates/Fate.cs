using System;
using System.Numerics;
using BOCCHI.Data;
using BOCCHI.Enums;
using Dalamud.Memory;
using ECommons.DalamudServices;
using Ocelot.Modules;
using FateContext = FFXIVClientStructs.FFXIV.Client.Game.Fate.FateContext;

namespace BOCCHI.Modules.Fates;

/// <summary>
/// 一個被追蹤的 FATE。刻意不保存 FATE 表的位址：FATE 結束、換區或表輪替後那塊記憶體會被回收，隔幀再讀不是垃圾值就是 try/catch 攔不到的崩潰。
/// 建構與 <see cref="Refresh" /> 當下把一生不變的欄位（id、名稱、半徑、座標）複製成受管值；只有進度在每次存取時用 Id 去 Svc.Fates 重查，查不到回最後一次讀到的值。
/// 物件活得比 FATE 久是常態：FateTracker 的 OnFateDespawned 拿已經不在表上的 Fate 去通知（Alerter 會讀 Name），FateActivity 在條目移出 FateTracker.Fates 之後還讀 Radius／StartPosition／Name。
/// 生命週期是「一個 FATE 從出現到消失」：FateTracker 只在第一次看到某個 id 時建它，之後每幀 Refresh，<see cref="Progress" /> 才累積得出兩筆以上樣本（ETA 的前提）。
/// </summary>
public class Fate
{
    /// <summary>遊戲還沒給名稱時 <see cref="Name" /> 回的後備字樣。</summary>
    public const string UnknownName = "Unknown Fate";

    public readonly EventData Data;

    /// <summary>FATE id。建構時複製成受管值,之後不再解參考,永遠有效。</summary>
    public readonly uint Id;

    /// <summary>
    /// 進度樣本序列。跨幀累積 —— 物件本身活過整個 FATE,所以這裡會累積到兩筆以上,
    /// <see cref="EventProgress.EstimateTimeToCompletion" /> 才算得出 ETA。
    /// </summary>
    public readonly EventProgress Progress = new();

    // 以下三個都是「FATE 一生不變」的值,在讀得到有效位址的那一幀複製成受管值,之後只讀副本。
    // 初值只是為了滿足編譯器的明確賦值分析(它看不進 Refresh);建構式一定會立刻蓋掉它。
    private string capturedName = string.Empty;

    private float capturedRadius;

    private Vector3 capturedStartPosition;

    // 唯一會變的欄位:最後一次成功從 FATE 表讀到的進度。
    private byte lastKnownProgress;

    private int capturedStartTimeEpoch;

    // 上次解析名稱時的原始位元組：名稱只由這段位元組決定，沒變就沿用 capturedName，不每幀重新解析配置。
    private byte[]? capturedNameBytes;

    public unsafe Fate(FateContext* fate)
    {
        Id = fate->FateId;
        Data = EventData.Fates[Id];

        Refresh(fate);
    }

    /// <summary>
    /// 用這一幀剛從 Svc.Fates 讀到的位址重新複製一次快照值。只在呼叫當下使用 <paramref name="fate" />、不保存；呼叫端必須保證傳進來的是本幀剛讀到、非 0 的位址。
    /// 每幀都複製而不是只在建構時複製一次：第一幀讀到還沒填好的值（例如名稱是空字串）才不會被永遠固定住。
    /// </summary>
    public unsafe void Refresh(FateContext* fate)
    {
        var nameBytes = fate->Name.AsSpan();
        if (capturedNameBytes == null || !nameBytes.SequenceEqual(capturedNameBytes))
        {
            capturedName = MemoryHelper.ReadSeString(&fate->Name).ToString();
            capturedNameBytes = nameBytes.ToArray();
        }

        capturedRadius = fate->Radius;
        capturedStartPosition = fate->Location;
        lastKnownProgress = fate->Progress;
        capturedStartTimeEpoch = fate->StartTimeEpoch;
    }

    public string Name
    {
        get => string.IsNullOrEmpty(capturedName) ? UnknownName : capturedName;
    }

    public float Radius
    {
        get => Data.Radius ?? capturedRadius;
    }

    public Vector3 StartPosition
    {
        get => Data.StartPosition ?? capturedStartPosition;
    }

    /// <summary>遊戲記錄的開始時間（Unix 秒）；0 以下＝遊戲還沒給。</summary>
    public int StartTimeEpoch
    {
        get => capturedStartTimeEpoch;
    }

    /// <summary>
    /// 目前進度(0~100)。每次存取都用 Id 去 Svc.Fates 重查,不依賴任何跨幀指標。
    /// FATE 已經不在表上(結束/換區)時回最後一次成功讀到的進度。
    /// 照索引讀位址、不建列舉器與包裝；解參條件與 Dalamud FateTable.CreateFateReference 相同
    /// (位址非 0、PlayerState 已載入)，PlayerState 未載入時一樣回最後一次讀到的進度。
    /// </summary>
    public unsafe byte CurrentProgress
    {
        get
        {
            var table = Svc.Fates;
            var count = table.Length;
            if (count == 0 || !Svc.PlayerState.IsLoaded)
            {
                return lastKnownProgress;
            }

            for (var i = 0; i < count; i++)
            {
                var live = (FateContext*)table.GetFateAddress(i);
                if (live != null && live->FateId == Id)
                {
                    lastKnownProgress = live->Progress;
                    return lastKnownProgress;
                }
            }

            return lastKnownProgress;
        }
    }

    public void Update(UpdateContext context)
    {
        // 一幀只讀一次：CurrentProgress 會走訪整張 FATE 表。
        var progress = CurrentProgress;

        if (progress <= 0)
        {
            return;
        }

        if (Progress.Count == 0 || Progress.Latest != progress)
        {
            Progress.Add(progress);
        }
    }

    public bool IsPotFate()
    {
        return Data.Note == MonsterNote.PersistentPots;
    }

    public Aethernet GetAethernet()
    {
        return Data.Aethernet ?? ZoneData.GetClosestAethernetShard(StartPosition);
    }
}
