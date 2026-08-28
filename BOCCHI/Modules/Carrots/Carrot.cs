using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;

namespace BOCCHI.Modules.Carrots;

public class Carrot(IGameObject obj)
{
    public static Vector4 Color { get; } = new(0.2f, 0.8f, 0.2f, 1f);

    // ⚠️ 不要把 IGameObject 存進欄位跨幀用。
    // 主建構子參數 obj 若被方法本體讀取，編譯器會產生隱藏的捕獲欄位，
    // 效果等同把包裝存起來。本 pin 的 ObjectTable 對每個格位×kind
    // 預配一個包裝實例、存取時就地改寫 Address（ObjectTable.cs:198-232），
    // 格位空掉時則完全不改寫 —— 跨幀持有會靜默換成別的物件或懸空。
    // CarrotsTracker.Tick 在 framework tick 重建清單，Radar.Draw 卻在 Render
    // 事件讀取，兩者不同幀 ⇒ 捕獲欄位就是一根跨幀原生指標。
    // 正解：建構當下抄 GameObjectId，用時重查物件表。
    private readonly ulong gameObjectId = obj.GameObjectId;

    // 建構當下抄一份純量座標當快照。Carrot 是靜態物件，
    // 查不到時回快照最差只是多畫一幀舊位置，不會崩潰。
    private Vector3 lastKnownPosition = obj.Position;

    private IGameObject? Resolve()
    {
        return Svc.Objects.SearchById(gameObjectId);
    }

    public bool IsValid()
    {
        // 重查物件表本身就是有效性檢查：查不到代表這顆 Carrot 已經不在了。
        // （原本的 obj.IsValid() 只會回報「玩家有沒有登入」。）
        return Resolve() is { IsDead: false };
    }

    public Vector3 GetPosition()
    {
        // 查得到就順便更新快取；查不到就回最後已知座標。
        var resolved = Resolve();
        if (resolved != null)
        {
            lastKnownPosition = resolved.Position;
        }

        return lastKnownPosition;
    }
}
