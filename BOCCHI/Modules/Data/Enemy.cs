using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace BOCCHI.Modules.Data;

/// <summary>
/// 敵人資料的**快照**。
///
/// 🔴 原本這個類別把 <c>obj.Address</c> 快取成 <c>BattleChara*</c> 欄位,再由每個屬性延後解參考。
/// 那是跨幀保存原生指標,而且消費端更糟:<c>Api.SendEnemyData</c> 是 async 的,
/// <c>EnemyDataHelper.MarkSharedData(enemy)</c> 排在 <c>await client.PostAsync(...)</c> **之後**,
/// 也就是一趟網路往返(可能好幾秒)之後才在執行緒集區上讀 <c>BattleChara->LayoutId</c>。
/// 那時玩家可能已經換區、該物件早被回收 —— 讀到垃圾算好的,頁面若已解除對映就是
/// AccessViolationException,而 AVE 在 .NET Core 屬於 corrupted-state exception,
/// <c>try/catch</c> 完全攔不到(<c>SendEnemyData</c> 那個 catch 救不了)。
/// 另外 <c>IGameObject.Address</c> 是建構當下就凍結的,<c>IsValid()</c> 只回答「玩家登入了沒」,
/// 兩者都不構成防護。
///
/// 正解是不保存指標:建構當下(呼叫端仍在遊戲執行緒、物件剛從物件表取出)一次把值全部取完,
/// 之後所有讀取都只碰受管理的欄位。所有消費端(<c>MonsterPayload.Create</c>、
/// <c>EnemyDataHelper</c>)拿到的值與原本完全相同 —— LayoutId 之於同一個物件是不變的。
/// </summary>
public unsafe class Enemy
{
    public uint LayoutId { get; }

    public uint Rank { get; }

    public string Name { get; }

    public Position Position { get; }

    public Enemy(IGameObject obj)
    {
        Name = obj.Name.ToString();
        Position = Data.Position.Create(obj.Position);

        var battleChara = (BattleChara*)obj.Address;
        if (battleChara == null)
        {
            return;
        }

        LayoutId = battleChara->LayoutId;
        Rank = battleChara->ForayInfo.Level;
    }
}
