namespace BOCCHI.Modules.Treasure;

/// <summary>
/// 一條開箱步驟記開箱數的規則：這條步驟看過寶箱還沒開、之後才看到開了，才記一次（每條最多一次）。
/// 開過的寶箱還選得到；開箱步驟開始時就看到已開的不記，也不再互動（<see cref="ShouldInteract"/>）。純邏輯、只在框架執行緒用。
/// </summary>
public sealed class ChestOpenWatch
{
    private bool sawClosed;

    private bool counted;

    /// <summary>每次讀到寶箱的已開旗標就呼叫（互動前後都可以）。</summary>
    public void See(bool opened)
    {
        if (!opened)
        {
            sawClosed = true;
        }
    }

    /// <summary>互動前讀到已開旗標時呼叫（含 <see cref="See"/>）：回 false＝寶箱已經開了，這一拍不互動、步驟結束。</summary>
    public bool ShouldInteract(bool opened)
    {
        See(opened);
        return !opened;
    }

    /// <summary>確定寶箱開了時呼叫：看過它沒開、而且還沒記過才回 true。</summary>
    public bool TakeOpened()
    {
        if (!sawClosed || counted)
        {
            return false;
        }

        counted = true;
        return true;
    }
}
