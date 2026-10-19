namespace BOCCHI.Modules.Teleporter;

/// <summary>BOCCHI 在確認框 PreSetup 當下接手的那一扇：記下位址與內文，之後只按同一位址、同一句的那一扇。</summary>
/// <remarks>純邏輯、不碰遊戲記憶體（位址只做等值比較、從不解參），離線驗收直接編譯這個檔。只在框架執行緒用。</remarks>
public sealed class YesnoClaim
{
    public nint Address { get; private set; }

    public string? Prompt { get; private set; }

    /// <summary>Environment.TickCount64 的時刻，過了還沒按就放棄；沒接手時是 0。</summary>
    public long PressUntil { get; private set; }

    public bool IsActive
    {
        get => Address != 0;
    }

    public void Claim(nint address, string prompt, long pressUntil)
    {
        Address = address;
        Prompt = prompt;
        PressUntil = pressUntil;
    }

    /// <summary>每一扇確認框的 PreSetup 都先呼叫：同一位址又開了一扇＝接手的那扇已經不在了。回 true＝因此放掉接手。</summary>
    public bool NoteSetup(nint address)
    {
        if (!IsActive || address != Address)
        {
            return false;
        }

        Clear();
        return true;
    }

    public bool IsExpired(long now)
    {
        return IsActive && now > PressUntil;
    }

    /// <summary>現在取到的這一扇（位址與內文）是不是接手的那一扇。</summary>
    public bool Matches(nint address, string? prompt)
    {
        return IsActive && address == Address && prompt == Prompt;
    }

    public void Clear()
    {
        Address = 0;
        Prompt = null;
        PressUntil = 0;
    }
}
