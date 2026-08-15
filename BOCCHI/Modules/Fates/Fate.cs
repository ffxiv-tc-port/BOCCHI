using System.Numerics;
using BOCCHI.Data;
using BOCCHI.Enums;
using Dalamud.Game.ClientState.Fates;
using ECommons.DalamudServices;
using Ocelot.Modules;

namespace BOCCHI.Modules.Fates;

/// <summary>
/// 一個被追蹤的 FATE。
///
/// 🔴 這個類別刻意「不保存 IFate」。
/// IFate 的原生位址(FateContext*)是建構當下凍結的,之後永不重新解析。FATE 一結束、
/// 換區、或 FATE 表輪替,那塊記憶體就會被回收再利用 —— 隔幀再讀到的不是垃圾值就是崩潰。
/// 而 AccessViolationException 在 .NET Core 是 corrupted-state exception,
/// try/catch 攔不到,所以原本包在每個屬性外面的五個 catch 全是死碼(而且「讀到被回收的
/// 記憶體」多數時候根本不會擲例外,是靜默回垃圾值,連死碼的假象都不會出現)。
///
/// 正解是不要讓跨幀存取有原生指標可解:
///   1. 建構當下(IFate 剛從 Svc.Fates 拿到,指標必定有效)把「FATE 一生不變」的欄位
///      複製成受管值 —— id、名稱、半徑、座標。之後再也不碰原生記憶體。
///   2. 只有會隨時間變動的「進度」在每次存取時用 Id 去 Svc.Fates 重查;
///      查不到就回最後一次成功讀到的值(建構時就取得初值,所以永遠有值可回)。
///
/// 「物件活得比 FATE 久」在這個外掛裡是常態不是例外,所以上面那條是必要的而不是保險:
///   - FateTracker 的 OnFateDespawned 是拿「上一幀的 Fate 物件」去通知的,依定義那個
///     FATE 已經不在表上了,而 Alerter.OnFateDespawned 會去讀它的 Name。
///   - FateActivity 會活得比 FateTracker.Fates 裡的條目久(見該檔 GetRadius 的註解),
///     並在那之後讀 Radius / StartPosition / Name。
/// </summary>
public class Fate
{
    public readonly EventData Data;

    /// <summary>FATE id。建構時複製成受管值,之後不再解參考,永遠有效。</summary>
    public readonly uint Id;

    public readonly EventProgress Progress = new();

    // 以下三個都是「FATE 一生不變」的值,建構當下就從原生記憶體複製出來,之後只讀副本。
    private readonly string capturedName;

    private readonly float capturedRadius;

    private readonly Vector3 capturedStartPosition;

    // 唯一會變的欄位:最後一次成功從 FATE 表讀到的進度。
    private byte lastKnownProgress;

    public Fate(IFate fate)
    {
        Id = fate.FateId;
        Data = EventData.Fates[Id];

        capturedName = fate.Name.ToString();
        capturedRadius = fate.Radius;
        capturedStartPosition = fate.Position;
        lastKnownProgress = fate.Progress;
    }

    public string Name
    {
        get => string.IsNullOrEmpty(capturedName) ? "Unknown Fate" : capturedName;
    }

    public float Radius
    {
        get => Data.Radius ?? capturedRadius;
    }

    public Vector3 StartPosition
    {
        get => Data.StartPosition ?? capturedStartPosition;
    }

    /// <summary>
    /// 目前進度(0~100)。每次存取都用 Id 去 Svc.Fates 重查,不依賴任何跨幀指標。
    /// FATE 已經不在表上(結束/換區)時回最後一次成功讀到的進度。
    /// </summary>
    public byte CurrentProgress
    {
        get
        {
            foreach (var live in Svc.Fates)
            {
                if (live.FateId == Id)
                {
                    lastKnownProgress = live.Progress;
                    return lastKnownProgress;
                }
            }

            return lastKnownProgress;
        }
    }

    public void Update(UpdateContext context)
    {
        // 一幀只讀一次:CurrentProgress 會走訪整張 FATE 表,而原本這個方法會讀它三次。
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
