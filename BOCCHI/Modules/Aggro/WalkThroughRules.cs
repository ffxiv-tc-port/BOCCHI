using System;
using System.Numerics;
using BOCCHI.Data;

namespace BOCCHI.Modules.Aggro;

// 本檔假設與契約：
// 走路穿越的純判斷與狀態機，不碰遊戲、不配置，離線測試直接呼叫；只在框架執行緒用（不加鎖）。
// 只對「聽覺」那一個區域適用：同一隻怪的其他感知（視覺、範圍、魔法、血量、未知）各是一個區域，照舊繞開。
// 時間一律由呼叫端給（Environment.TickCount64），「發生過沒」另用旗標記，不拿 0 當「沒有」。

/// <summary>為什麼這一拍不要走路（給切回跑步那一行 log）。</summary>
public enum WalkStopReason : byte
{
    None = 0,
    Disabled = 1,
    Unavailable = 2,
    NotTravelling = 3,
    NotMoving = 4,
    Mounted = 5,
    Combat = 6,
    Passed = 7,
    Paused = 8,
}

/// <summary>趕路迴避這一拍對走路的要求；Want 為 true 時後面幾欄是觸發的那隻怪（Distance／Radius 單位米、X/Z 平面）。</summary>
public readonly record struct WalkRequest(bool Want, WalkStopReason Reason, ulong ZoneObjectId, uint NameId, int Level, float Distance, float Radius)
{
    public static WalkRequest Off(WalkStopReason reason)
    {
        return new WalkRequest(false, reason, 0, 0, 0, 0f, 0f);
    }
}

public enum WalkSend : byte
{
    None = 0,
    ToWalk = 1,
    ToRun = 2,
}

public enum WalkEvent : byte
{
    None = 0,

    /// <summary>確認切成走路了（我們按的）。</summary>
    Walking = 1,

    /// <summary>確認切回跑步了（我們按的）。</summary>
    Restored = 2,

    /// <summary>要走路時你本來就在走路：不切換、之後也不切回跑步（每一段只報一次）。</summary>
    AlreadyWalking = 3,

    /// <summary>我們切成走路之後，你自己切回跑步：這一趟不再切成走路。</summary>
    UserRan = 4,

    /// <summary>該切回跑步時已經是跑步（你自己切的，或遊戲切的）：不用再按。</summary>
    UserRestored = 5,

    /// <summary>按了 <see cref="WalkToggle.MaxAttempts"/> 次都沒變：<see cref="WalkToggle.GiveUpMs"/> 內不再按。</summary>
    GaveUp = 6,
}

public static class WalkThroughRules
{
    /// <summary>沿路線往前看這麼多米（3D 弧長）：會穿過聽覺範圍就先切成走路。</summary>
    public const float LookaheadM = 5f;

    /// <summary>已經在走路時，離聽覺範圍（X/Z）這麼近仍算還沒走出去，避免在邊界來回切換。</summary>
    public const float HoldM = 1f;

    /// <summary>高速時前瞻至少留這麼多秒：按鍵到遊戲真的切成走路要一點時間，騎乘時 <see cref="LookaheadM"/> 米不到 0.2 秒。</summary>
    public const float LookaheadSeconds = 0.35f;

    /// <summary>前瞻的上限：提早切成走路的那一段是用走的，不能太長。</summary>
    public const float LookaheadMaxM = 12f;

    public static bool IsWalkable(in AggroZone zone)
    {
        return zone.Sense == AggroSense.Sound;
    }

    /// <summary>照目前速度（米／秒，量不到＝0）算前瞻距離：<see cref="LookaheadM"/>～<see cref="LookaheadMaxM"/>。</summary>
    public static float Lookahead(float speed)
    {
        return Math.Clamp(speed * LookaheadSeconds, LookaheadM, LookaheadMaxM);
    }

    /// <summary>
    /// 這一拍走路穿越能不能用（None＝能）：設定關、戰鬥中（已經被發現：照跑，聽覺範圍改回繞開）、按鍵或狀態暫時不能用。
    /// 騎乘與否不在條件裡：騎著切成走路同樣不會被聽覺發現，不下坐騎。
    /// </summary>
    public static WalkStopReason Gate(bool enabled, bool inCombat, bool available)
    {
        return !enabled ? WalkStopReason.Disabled
            : inCombat ? WalkStopReason.Combat
            : available ? WalkStopReason.None
            : WalkStopReason.Unavailable;
    }

    /// <summary>有沒有狀態不低於 minState、離玩家（X/Z、同一層）不超過 distance 的聽覺範圍；給呼叫端判斷要不要去讀路徑。</summary>
    public static bool AnyNear(ReadOnlySpan<AggroZone> zones, AggroState minState, Vector3 position, ulong excludeId, float distance)
    {
        minState = minState == AggroState.None ? AggroState.Possible : minState;
        foreach (ref readonly var zone in zones)
        {
            if (Eligible(zone, minState, excludeId) && AggroZones.InBand(zone, position.Y) && AggroZones.Distance2D(zone, position) <= distance)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 要走路穿越的第一個聽覺範圍：玩家離它（X/Z、同一層）不超過 holdM（在裡面＝0），或沿 path 往前 lookaheadM 米內會碰到；沒有回 −1。
    /// excludeId＝目前目標（要走過去打的那隻不必走路）。
    /// </summary>
    public static int Find(ReadOnlySpan<AggroZone> zones, AggroState minState, Vector3 position, ReadOnlySpan<Vector3> path, ulong excludeId, float lookaheadM, float holdM)
    {
        minState = minState == AggroState.None ? AggroState.Possible : minState;
        for (var i = 0; i < zones.Length; i++)
        {
            ref readonly var zone = ref zones[i];
            if (!Eligible(zone, minState, excludeId))
            {
                continue;
            }

            if (AggroZones.InBand(zone, position.Y) && AggroZones.Distance2D(zone, position) <= MathF.Max(holdM, 0f))
            {
                return i;
            }

            if (PathHits(zone, position, path, lookaheadM))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 這一拍要不要走路：要求超過 maxAgeMs 沒更新（BOCCHI 暫停更新：過場、讀取、對話、離開新月島）就當成不要，原因＝Paused。
    /// </summary>
    public static bool EffectiveWant(bool hasRequest, long requestAt, long now, long maxAgeMs, in WalkRequest request, out WalkStopReason reason)
    {
        if (!hasRequest || now - requestAt > maxAgeMs)
        {
            reason = WalkStopReason.Paused;
            return false;
        }

        reason = request.Want ? WalkStopReason.None : request.Reason;
        return request.Want;
    }

    /// <summary>插件卸載時要不要替你按一次切回跑步：是我們切的、遊戲沒在關閉、在框架執行緒、有能用的按鍵（還要現在真的在走路，由呼叫端讀）。</summary>
    public static bool ShouldRestoreOnDispose(bool owned, bool frameworkUnloading, bool frameworkThread, bool keyUsable)
    {
        return owned && !frameworkUnloading && frameworkThread && keyUsable;
    }

    private static bool Eligible(in AggroZone zone, AggroState minState, ulong excludeId)
    {
        return IsWalkable(zone) && zone.State >= minState && (excludeId == 0 || zone.GameObjectId != excludeId);
    }

    private static bool PathHits(in AggroZone zone, Vector3 start, ReadOnlySpan<Vector3> path, float horizon)
    {
        var previous = start;
        var remaining = horizon > 0f ? horizon : 0f;
        foreach (var point in path)
        {
            if (remaining <= 0f)
            {
                return false;
            }

            var length = Vector3.Distance(previous, point);
            var end = length > remaining && length > 0f ? Vector3.Lerp(previous, point, remaining / length) : point;
            if (AggroZones.SegmentHits(zone, previous, end))
            {
                return true;
            }

            remaining -= length;
            previous = point;
        }

        return false;
    }

    /// <summary>
    /// 從遊戲的「切換奔跑與行走」兩組鍵盤設定挑一組能送的：沒有按鍵、滑鼠鍵、單按修飾鍵、移動鍵（會被當成你在操作）都不用；
    /// 兩組都能用時挑修飾鍵少的。回 false＝沒有能用的。
    /// </summary>
    public static bool TryChooseKey(byte key1, byte modifiers1, byte key2, byte modifiers2, out byte key, out byte modifiers)
    {
        var ok1 = Usable(key1);
        var ok2 = Usable(key2);
        if (ok1 && (!ok2 || ModifierCount(modifiers1) <= ModifierCount(modifiers2)))
        {
            (key, modifiers) = (key1, modifiers1);
            return true;
        }

        if (ok2)
        {
            (key, modifiers) = (key2, modifiers2);
            return true;
        }

        (key, modifiers) = (0, 0);
        return false;
    }

    private static bool Usable(byte key)
    {
        return key switch
        {
            0 or 1 or 2 or 4 or 5 or 6 => false,
            0x10 or 0x11 or 0x12 => false,
            0x57 or 0x41 or 0x53 or 0x44 or 0x51 or 0x45 => false,
            >= 0x25 and <= 0x28 => false,
            _ => true,
        };
    }

    private static int ModifierCount(byte modifiers)
    {
        return (modifiers & 1) + ((modifiers >> 1) & 1) + ((modifiers >> 2) & 1);
    }
}

/// <summary>
/// 走路／跑步切換的狀態機。每拍呼叫 <see cref="Step"/>（want＝這一拍要不要走路、walking＝遊戲現在是不是走路、canSend＝這一拍能不能按鍵），
/// 回傳要按哪個方向（呼叫端去按「切換奔跑與行走」）。只還原自己切的：你本來就在走路就不碰，離開時也不切回跑步。
/// 按了之後 <see cref="VerifyMs"/> 內讀到狀態變了才算數；連續 <see cref="MaxAttempts"/> 次沒變就 <see cref="GiveUpMs"/> 內不再按。
/// </summary>
public sealed class WalkToggle
{
    public const long VerifyMs = 1_000;

    public const int MaxAttempts = 3;

    public const long GiveUpMs = 60_000;

    private bool pending;

    private bool pendingWalk;

    private long sentAt;

    private bool gaveUp;

    private long gaveUpUntil;

    private bool notedAlreadyWalking;

    /// <summary>目前的走路是我們切的（之後要切回跑步）。</summary>
    public bool Owned { get; private set; }

    /// <summary>我們切成走路之後你自己切回跑步：這一趟不再切成走路（<see cref="ResetTrip"/> 清掉）。</summary>
    public bool UserOverride { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>最近一次 <see cref="Step"/> 產生的事件；放棄時 <see cref="GaveUpOnRun"/> 說是哪個方向放棄。</summary>
    public WalkEvent Event { get; private set; }

    public bool GaveUpOnRun { get; private set; }

    public bool Pending
    {
        get => pending;
    }

    /// <summary>是我們切的，或剛按下切成走路還沒確認（插件卸載時兩者都要切回）。</summary>
    public bool MayOwn
    {
        get => Owned || (pending && pendingWalk);
    }

    /// <summary>這一趟可以走路穿越：沒在放棄期間、你沒有自己切回跑步。</summary>
    public bool Available(long now)
    {
        return !(gaveUp && now < gaveUpUntil) && !UserOverride;
    }

    public void ResetTrip()
    {
        UserOverride = false;
    }

    public WalkSend Step(long now, bool want, bool walking, bool canSend)
    {
        Event = WalkEvent.None;
        if (pending)
        {
            if (walking == pendingWalk)
            {
                pending = false;
                Attempts = 0;
                Owned = pendingWalk;
                Event = pendingWalk ? WalkEvent.Walking : WalkEvent.Restored;
                return WalkSend.None;
            }

            if (now - sentAt < VerifyMs)
            {
                return WalkSend.None;
            }

            pending = false;
            if (++Attempts >= MaxAttempts)
            {
                Attempts = 0;
                gaveUp = true;
                gaveUpUntil = now + GiveUpMs;
                GaveUpOnRun = !pendingWalk;
                Event = WalkEvent.GaveUp;
                return WalkSend.None;
            }
        }

        if (gaveUp)
        {
            if (now < gaveUpUntil)
            {
                return WalkSend.None;
            }

            gaveUp = false;
        }

        if (want && !UserOverride)
        {
            if (Owned)
            {
                if (!walking)
                {
                    Owned = false;
                    UserOverride = true;
                    Event = WalkEvent.UserRan;
                }

                return WalkSend.None;
            }

            if (walking)
            {
                if (!notedAlreadyWalking)
                {
                    notedAlreadyWalking = true;
                    Event = WalkEvent.AlreadyWalking;
                }

                return WalkSend.None;
            }

            return canSend ? Send(now, true) : WalkSend.None;
        }

        if (!want)
        {
            notedAlreadyWalking = false;
        }

        if (!Owned)
        {
            return WalkSend.None;
        }

        if (!walking)
        {
            Owned = false;
            Event = WalkEvent.UserRestored;
            return WalkSend.None;
        }

        return canSend ? Send(now, false) : WalkSend.None;
    }

    private WalkSend Send(long now, bool toWalk)
    {
        pending = true;
        pendingWalk = toWalk;
        sentAt = now;
        return toWalk ? WalkSend.ToWalk : WalkSend.ToRun;
    }
}
