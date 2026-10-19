using System;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using ECommons.DalamudServices;

namespace BOCCHI.Modules.Automator;

public enum PotHuntEvent : byte
{
    None,

    /// <summary>EurekaHelper 開始尋寶：開始等。</summary>
    HuntStarted,

    /// <summary>尋寶結束：不再等。</summary>
    HuntEnded,

    /// <summary>魔法甕結束後等了 <see cref="PotHuntYield.StartGraceMs"/> 都沒開始尋寶。</summary>
    GraceExpired,

    /// <summary>一輪尋寶超過 <see cref="PotHuntYield.HuntCapMs"/>：這一輪不再等。</summary>
    CapReached,

    /// <summary>等的途中問不到了（插件卸載、端點不見、擲例外）。</summary>
    Lost,
}

/// <summary>
/// 魔法甕打完後先讓 EurekaHelper 的尋寶走完：要不要先不選活動、不移動。純邏輯、不碰遊戲（離線驗收直接呼叫）；只在框架執行緒用。
/// 問不到（沒裝、端點不在、擲例外）一律當成沒在尋寶，不會擋住非法模式。
/// </summary>
public sealed class PotHuntYield
{
    /// <summary>魔法甕結束後等 EurekaHelper 收到第一則財寶提示的上限（實機 1.4～2.2 秒）。</summary>
    public const long StartGraceMs = 5_000;

    /// <summary>
    /// 一輪尋寶最多等這麼久；超過就先繼續，對方變回 false 之前不再等。比 EurekaHelper 自己的整輪上限（300 秒）多留 10 秒：
    /// 它還在找時不中途收回（收回會把正在追寶的你拉下坐騎施放返回），只在它的旗標卡住時才用到。
    /// </summary>
    public const long HuntCapMs = 310_000;

    // 時間一律用呼叫端給的 now（Environment.TickCount64），狀態另用旗標，不拿 0 當「沒有」。
    private bool graceArmed;

    private long graceFrom;

    private bool hunting;

    private long huntSince;

    private bool capped;

    public bool IsGraceArmed
    {
        get => graceArmed;
    }

    public bool IsHunting
    {
        get => hunting;
    }

    /// <summary>魔法甕 F.A.T.E. 結束時呼叫：接下來 <see cref="StartGraceMs"/> 內先等 EurekaHelper 開始尋寶。尋寶中、上限後不理。</summary>
    public void ArmGrace(long now)
    {
        if (!hunting && !capped)
        {
            graceArmed = true;
            graceFrom = now;
        }
    }

    public void Reset()
    {
        graceArmed = false;
        hunting = false;
        capped = false;
    }

    /// <param name="available">這次問得到 EurekaHelper.OccultPotHuntActive。</param>
    /// <param name="active">它回的值；<paramref name="available"/> 為 false 時不看。</param>
    /// <param name="heldMs">這次事件之前已經等了多久（沒有事件時是 0）。</param>
    /// <returns>true＝先不選下一個活動、不移動。</returns>
    public bool Step(long now, bool available, bool active, out PotHuntEvent ev, out long heldMs)
    {
        ev = PotHuntEvent.None;
        heldMs = 0;
        if (!available)
        {
            if (hunting)
            {
                ev = PotHuntEvent.Lost;
                heldMs = now - huntSince;
            }

            Reset();
            return false;
        }

        if (capped)
        {
            capped = active;
            return false;
        }

        if (active)
        {
            if (!hunting)
            {
                hunting = true;
                huntSince = now;
                graceArmed = false;
                ev = PotHuntEvent.HuntStarted;
                return true;
            }

            if (now - huntSince <= HuntCapMs)
            {
                return true;
            }

            ev = PotHuntEvent.CapReached;
            heldMs = now - huntSince;
            hunting = false;
            capped = true;
            return false;
        }

        if (hunting)
        {
            ev = PotHuntEvent.HuntEnded;
            heldMs = now - huntSince;
            hunting = false;
            return false;
        }

        if (!graceArmed)
        {
            return false;
        }

        if (now - graceFrom < StartGraceMs)
        {
            return true;
        }

        ev = PotHuntEvent.GraceExpired;
        heldMs = now - graceFrom;
        graceArmed = false;
        return false;
    }
}

/// <summary>EurekaHelper.OccultPotHuntActive（Func&lt;bool&gt;，對方框架執行緒寫的快照、不阻塞）。只在框架執行緒呼叫。</summary>
/// <param name="fallback">問不到時記錄裡說「什麼照常」（非法模式、自動返回各一個實例）。</param>
public sealed class EurekaPotHuntIpc(string fallback = "非法模式照常")
{
    public const string Endpoint = "EurekaHelper.OccultPotHuntActive";

    private ICallGateSubscriber<bool>? subscriber;

    private bool failureReported;

    /// <returns>false＝問不到（沒裝、端點不在、任何例外），<paramref name="active"/> 一律 false。</returns>
    public bool TryRead(out bool active)
    {
        active = false;
        try
        {
            subscriber ??= Svc.PluginInterface.GetIpcSubscriber<bool>(Endpoint);
            if (!subscriber.HasFunction)
            {
                return false;
            }

            active = subscriber.InvokeFunc();
            return true;
        }
        catch (IpcNotReadyError)
        {
            return false;
        }
        catch (Exception ex)
        {
            active = false;
            if (!failureReported)
            {
                failureReported = true;
                Svc.Log.Information($"[BOCCHI][魔法甕尋寶] 問 {Endpoint} 失敗（{ex.GetType().Name}：{ex.Message}）：當成沒在尋寶，{fallback}。");
            }

            return false;
        }
    }
}
