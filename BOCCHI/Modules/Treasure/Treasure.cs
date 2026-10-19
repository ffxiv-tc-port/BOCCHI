using System.Linq;
using System.Numerics;
using BOCCHI.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Ocelot;
using XIVTreasure = Lumina.Excel.Sheets.Treasure;
using TreasureFlags = FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure.TreasureFlags;

namespace BOCCHI.Modules.Treasure;

public class Treasure(IGameObject obj)
{
    // 不要把 IGameObject 存進欄位跨幀用：本 pin 的 ObjectTable 每個格位×kind 預配一個包裝、存取時就地改寫 Address，跨幀持有會靜默換成別的物件或懸空；
    // IGameObject.IsValid() 只檢查玩家有沒有登入、不驗位址。TreasureTracker 會把 Treasure 跨幀留著，
    // 寶箱消失後 CheckOpened() 解參考已釋放的記憶體是 try/catch 攔不到的。
    // 所以存 GameObjectId，每次用時重查物件表，查不到就中止。
    private readonly ulong gameObjectId = obj.GameObjectId;

    // DataId 只是純量,建構時複製一份即可,不需要每次解參考。
    private readonly uint dataId = obj.BaseId;

    private Vector3 lastKnownPosition = obj.Position;

    // 追蹤鍵用 GameObjectId 而非 DataId:同型寶箱共用 DataId,
    // 用 DataId 當鍵會讓已消失寶箱的陳舊項目被「同型的另一個寶箱」保住。
    public ulong Id
    {
        get => gameObjectId;
    }

    /// <summary>最近一次 <see cref="GetPosition"/> 查到的座標，不重查物件表（TreasureTracker 每幀排序時會更新）。</summary>
    public Vector3 LastKnownPosition
    {
        get => lastKnownPosition;
    }

    private TreasureFlags LastFlags = TreasureFlags.None;

    // 種類只看資料表（dataId 固定），查到一次就不再查。
    private TreasureType? treasureType;

    /// <summary>最近一次 <see cref="CheckOpened"/> 讀到已開旗標（TreasureTracker 每幀更新），不重查物件表。</summary>
    public bool IsOpened
    {
        get => LastFlags.HasFlag(TreasureFlags.Opened);
    }

    private IGameObject? Resolve()
    {
        return Svc.Objects.SearchById(gameObjectId);
    }

    public unsafe bool CheckOpened()
    {
        var resolved = Resolve();
        if (resolved == null)
        {
            return false;
        }

        var gameObject = (GameObject*)(void*)resolved.Address;
        if (gameObject == null)
        {
            return false;
        }

        var instance = (FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure*)gameObject;
        var currentFlags = instance->Flags;

        if (currentFlags != LastFlags)
        {
            var wasNotOpened = !LastFlags.HasFlag(TreasureFlags.Opened);
            var isNowOpened = currentFlags.HasFlag(TreasureFlags.Opened);

            LastFlags = currentFlags;

            if (wasNotOpened && isNowOpened)
            {
                return true;
            }
        }

        return false;
    }


    private XIVTreasure? GetData()
    {
        // GetRowOrDefault 是 O(1) 且不配置；不要 ToList() 整張表再線性掃描。
        return Svc.Data.GetExcelSheet<XIVTreasure>().GetRowOrDefault(dataId);
    }

    public bool IsValid()
    {
        // 重查物件表本身就是有效性檢查：查不到代表這個寶箱已經不在了（IsValid() 只回報玩家有沒有登入）。
        return Resolve() is { IsDead: false, IsTargetable: true };
    }

    public Vector3 GetPosition()
    {
        // 查得到就順便更新快取;查不到就回最後已知座標。
        // 回傳純量而非解參考,寶箱消失時最差只是多畫一幀舊位置,不會崩潰。
        var resolved = Resolve();
        if (resolved != null)
        {
            lastKnownPosition = resolved.Position;
        }

        return lastKnownPosition;
    }

    public TreasureType GetTreasureType()
    {
        if (treasureType is { } cached)
        {
            return cached;
        }

        var data = GetData();
        var type = (data?.SGB.RowId ?? 0) switch
        {
            1597 => TreasureType.Silver,
            1596 => TreasureType.Bronze,
            _ => TreasureType.Unknown,
        };

        if (data != null)
        {
            treasureType = type;
        }

        return type;
    }

    public Vector4 GetColor()
    {
        return GetTreasureType() switch
        {
            TreasureType.Bronze => TreasureModule.Bronze,
            TreasureType.Silver => TreasureModule.Silver,
            _ => TreasureModule.Unknown,
        };
    }

    public string GetName()
    {
        return I18N.T(GetTreasureType() switch
        {
            TreasureType.Bronze => "modules.treasure.panel.coffer.bronze",
            TreasureType.Silver => "modules.treasure.panel.coffer.silver",
            _ => "modules.treasure.panel.coffer.unknown",
        });
    }
}
