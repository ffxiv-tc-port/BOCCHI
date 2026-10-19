using System;
using System.Globalization;
using System.Numerics;
using BOCCHI.Automation;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using ECommons.DalamudServices;
using Ocelot.IPC;

namespace BOCCHI.Modules.Aggro;

// 本檔契約：只在框架執行緒（AggroModule.Update）執行。
// EurekaHelper 魔法甕尋寶的自動走向自己對 vnavmesh 下路線；BOCCHI 不另外下路線，只在 vnavmesh 正在走的路終點對得上
// EurekaHelper 最近一次送出的目的地時，把它登記成一趟趕路（TravelSource.EurekaPotHunt），之後的繞路、重算全由 AggroAvoidance 照自己的規則做。
// 使用者接手時 vnavmesh 會取消路線，AggroAvoidance 只在 vnavmesh 在走時動作，所以兩邊既有的讓位規則照舊。

public enum EurekaPotTravelDecision : byte
{
    /// <summary>EurekaHelper 沒有目的地，或 vnavmesh 沒在走／讀不到路線。</summary>
    Idle = 0,

    /// <summary>vnavmesh 走的不是 EurekaHelper 這次送的路（使用者或別的插件換了路）。</summary>
    NotTheirs = 1,

    /// <summary>目前的趕路登記已經對得上這條路。</summary>
    Covered = 2,

    Register = 3,
}

/// <summary>要不要把 vnavmesh 正在走的路登記成 EurekaHelper 尋寶的趕路。純邏輯、不碰遊戲。</summary>
public static class EurekaPotTravelRules
{
    /// <param name="sequence">EurekaHelper 第幾次自動走向；0＝目前沒有。</param>
    /// <param name="target">EurekaHelper 送出的目的地（只比 X/Z：地圖旗標沒有高度）。</param>
    /// <param name="pathEnd">vnavmesh 路線的最後一點；讀不到或空的是 null。</param>
    /// <param name="covered">目前還有效的趕路登記，終點已經對得上這條路。</param>
    public static EurekaPotTravelDecision Decide(int sequence, Vector3 target, bool running, Vector3? pathEnd, bool covered)
    {
        if (sequence == 0 || !running || pathEnd is not { } end)
        {
            return EurekaPotTravelDecision.Idle;
        }

        if (!AvoidanceRules.IsOwnPath(end, target))
        {
            return EurekaPotTravelDecision.NotTheirs;
        }

        return covered ? EurekaPotTravelDecision.Covered : EurekaPotTravelDecision.Register;
    }
}

/// <summary>EurekaHelper.OccultPotNavTarget：Func&lt;(int, Vector3, bool)&gt;，對方框架執行緒寫的快照、不阻塞。型別必須與提供端逐字相同。</summary>
public sealed class EurekaPotNavIpc
{
    public const string Endpoint = "EurekaHelper.OccultPotNavTarget";

    private ICallGateSubscriber<(int, Vector3, bool)>? subscriber;

    private bool failureReported;

    /// <returns>false＝問不到（沒裝、版本太舊沒有這個端點、任何例外）。</returns>
    public bool TryRead(out (int Sequence, Vector3 Destination, bool HasHeight) target)
    {
        target = default;
        try
        {
            subscriber ??= Svc.PluginInterface.GetIpcSubscriber<(int, Vector3, bool)>(Endpoint);
            if (!subscriber.HasFunction)
            {
                return false;
            }

            target = subscriber.InvokeFunc();
            return true;
        }
        catch (IpcNotReadyError)
        {
            return false;
        }
        catch (Exception ex)
        {
            target = default;
            if (!failureReported)
            {
                failureReported = true;
                Svc.Log.Information($"[BOCCHI][仇恨迴避] 問 {Endpoint} 失敗（{ex.GetType().Name}：{ex.Message}）：EurekaHelper 尋寶的自動走向照它原本的路走、不繞開仇恨範圍。");
            }

            return false;
        }
    }
}

/// <summary>把 EurekaHelper 尋寶自動走向的路線登記成趕路（見本檔契約）。</summary>
public sealed class EurekaPotTravel
{
    public const long CheckIntervalMs = 250;

    private const long VnavResolveRetryMs = 5_000;

    private readonly EurekaPotNavIpc ipc = new();

    private VNavmesh? vnav;

    private long vnavResolveAt;

    private long nextCheckAt;

    private int loggedSequence;

    private int adoptedSequence;

    private TravelIntentSnapshot adopted;

    public void Update(long now, AggroModule module)
    {
        if (now < nextCheckAt)
        {
            return;
        }

        nextCheckAt = now + CheckIntervalMs;
        if (!module.Config.AvoidWhileMoving || !ipc.TryRead(out var target) || target.Sequence == 0)
        {
            return;
        }

        // 這一次送出已經登記過、登記還有效：不必每拍讀路線。
        if (adoptedSequence == target.Sequence && TravelIntent.State.IsCurrent(adopted))
        {
            return;
        }

        if (!ResolveVnav(now, module))
        {
            return;
        }

        bool running;
        Vector3? end = null;
        try
        {
            running = vnav!.IsRunning();
            if (running)
            {
                var waypoints = vnav.GetWaypoints();
                if (waypoints is { Count: > 0 })
                {
                    end = waypoints[^1];
                }
            }
        }
        catch (Exception)
        {
            return;
        }

        var player = Svc.Objects.LocalPlayer;
        if (player == null)
        {
            return;
        }

        var state = TravelIntent.State;
        var covered = end is { } pathEnd
                      && state.TryGetActive(Svc.ClientState.TerritoryType, player.Position, now, out var current)
                      && AvoidanceRules.IsOwnPath(pathEnd, current.Destination);
        switch (EurekaPotTravelRules.Decide(target.Sequence, target.Destination, running, end, covered))
        {
            case EurekaPotTravelDecision.Covered:
                if (state.Current.Source == TravelSource.EurekaPotHunt)
                {
                    Adopt(target.Sequence, state.Current);
                }

                return;
            case EurekaPotTravelDecision.Register:
                TravelIntent.Register(end!.Value, TravelSource.EurekaPotHunt);
                Adopt(target.Sequence, state.Current);
                if (loggedSequence != target.Sequence)
                {
                    loggedSequence = target.Sequence;
                    var d = target.Destination;
                    var e = end.Value;
                    Svc.Log.Information(string.Create(CultureInfo.InvariantCulture,
                        $"[BOCCHI][仇恨迴避] EurekaHelper 魔法甕尋寶第 {target.Sequence} 次自動走向（{(target.HasHeight ? "3D 候選點" : "地圖旗標")} ({d.X:F1}, {d.Y:F1}, {d.Z:F1})）："
                        + $"vnavmesh 正在走這條路（終點 ({e.X:F1}, {e.Y:F1}, {e.Z:F1})），照 BOCCHI 趕路的規則繞開仇恨範圍；不另外下路線，你操作時照常讓你。"));
                }

                return;
            default:
                return;
        }
    }

    private void Adopt(int sequence, in TravelIntentSnapshot intent)
    {
        adoptedSequence = sequence;
        adopted = intent;
    }

    private bool ResolveVnav(long now, AggroModule module)
    {
        if (vnav != null)
        {
            return true;
        }

        if (now < vnavResolveAt)
        {
            return false;
        }

        vnavResolveAt = now + VnavResolveRetryMs;
        module.TryGetIPCSubscriber(out vnav);
        return vnav != null;
    }
}
