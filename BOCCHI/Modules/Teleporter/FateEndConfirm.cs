namespace BOCCHI.Modules.Teleporter;

public enum FateEndVerdict : byte
{
    /// <summary>沒有在確認。</summary>
    Idle,

    /// <summary>還在確認窗內、它還在 F.A.T.E. 清單上。</summary>
    Hold,

    /// <summary>它離開清單了：真的結束（打完或失敗）。</summary>
    Ended,

    /// <summary>確認窗過了它還在清單上：只是走出範圍，F.A.T.E. 還在進行。</summary>
    WalkedOut,
}

/// <summary>
/// 「打完自動返回」離開 F.A.T.E. 範圍時先確認它是不是真的結束。純邏輯、不碰遊戲（離線驗收直接呼叫）；只在框架執行緒用。
/// 確認窗沿用 <see cref="PotReturnWait.EndConfirmMs"/>：實機打完時離開範圍與離開清單差不到 0.1 秒。
/// </summary>
public sealed class FateEndConfirm
{
    private uint fateId;

    private string fateName = string.Empty;

    private long since;

    public bool IsPending
    {
        get => fateId != 0;
    }

    public uint FateId
    {
        get => fateId;
    }

    /// <param name="id">0 時不確認（呼叫端照舊處理）。</param>
    public void Begin(uint id, string name, long now)
    {
        fateId = id;
        fateName = name;
        since = now;
    }

    public void Cancel()
    {
        fateId = 0;
        fateName = string.Empty;
    }

    /// <param name="listed">那一場現在還在 F.A.T.E. 清單上。</param>
    /// <param name="id">有結論時是哪一場；<paramref name="heldMs"/> 是確認了多久。</param>
    public FateEndVerdict Step(long now, bool listed, out uint id, out string name, out long heldMs)
    {
        id = fateId;
        name = fateName;
        heldMs = now - since;
        if (fateId == 0)
        {
            heldMs = 0;
            return FateEndVerdict.Idle;
        }

        if (listed && heldMs < PotReturnWait.EndConfirmMs)
        {
            return FateEndVerdict.Hold;
        }

        Cancel();
        return listed ? FateEndVerdict.WalkedOut : FateEndVerdict.Ended;
    }
}
