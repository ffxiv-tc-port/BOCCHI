using System.Linq;
using System.Numerics;
using BOCCHI.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using XIVTreasure = Lumina.Excel.Sheets.Treasure;
using TreasureFlags = FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure.TreasureFlags;

namespace BOCCHI.Modules.Treasure;

public class Treasure(IGameObject obj)
{
    // ⚠️ 不要把 IGameObject 存進欄位跨幀用。
    // Dalamud 的 GameObject.Address 在建構時就凍結、永不重新解析
    // (GameObject.cs:137-139),而 IGameObject.IsValid() 只檢查「玩家有沒有登入」、
    // 完全不驗證位址(GameObject.cs:170-177)。TreasureTracker 會把 Treasure 跨幀留著,
    // 寶箱消失後 CheckOpened() 就是在解參考已釋放的記憶體——
    // 那是 corrupted-state exception,try/catch 攔不到。
    // 正解:存 GameObjectId,每次用時重查物件表,查不到就中止。
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

    private TreasureFlags LastFlags = TreasureFlags.None;

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
        // 原本 ToList() 會把整張 Treasure 表複製出來再線性掃描；GetRowOrDefault 是 O(1) 且不配置記憶體
        return Svc.Data.GetExcelSheet<XIVTreasure>().GetRowOrDefault(dataId);
    }

    public bool IsValid()
    {
        // 重查物件表本身就是有效性檢查:查不到代表這個寶箱已經不在了。
        // (原本的 obj.IsValid() 只會回報「玩家有沒有登入」。)
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

    private uint? GetModelId()
    {
        return GetData()?.SGB.RowId;
    }

    public TreasureType GetTreasureType()
    {
        switch (GetModelId() ?? 0)
        {
            case 1597:
                return TreasureType.Silver;
            case 1596:
                return TreasureType.Bronze;
            default:
                return TreasureType.Unknown;
        }
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
        return GetTreasureType() switch
        {
            TreasureType.Bronze => "Bronze Treasure Coffer",
            TreasureType.Silver => "Silver Treasure Coffer",
            _ => "Unknown Treasure Coffer",
        };
    }
}
