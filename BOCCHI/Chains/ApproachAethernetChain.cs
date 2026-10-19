using System;
using System.Numerics;
using BOCCHI.Automation;
using BOCCHI.Enums;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;
using Ocelot.IPC;

namespace BOCCHI.Chains;

/// <summary>
/// 走到魔路旁邊、朝自己這一側可以互動的位置，而不是走向魔路的中心點。
/// </summary>
/// <remarks>
/// 中心點在魔路的碰撞裡、不在導航網格上：vnavmesh 把終點落在離中心最近的那塊多邊形，
/// 營地魔路那塊在另一側，所以返回後會先往外走再繞回來。
/// 目的地在這一步開始時才算（返回會先傳回營地，建鏈當下的位置不是出發點）。
/// </remarks>
public class ApproachAethernetChain(VNavmesh vnav, Vector3 center) : ChainFactory
{
    private static readonly float[] Radii = [3f, 2.6f];

    // 必須小於 AethernetData.DISTANCE：走到之後呼叫端還要等「離中心夠近」才往下走。
    private const float MaxDistance = 3.4f;

    protected override Chain Create(Chain chain)
    {
        var destination = GetDestination(vnav, center);
        chain.Then(_ => TravelIntent.Register(destination, TravelSource.Aetheryte));
        chain.PathfindAndMoveTo(vnav, destination);
        if (destination == center)
        {
            return chain;
        }

        // 走到旁邊停下卻還不夠近（例如路徑點容許值被調大）時改走向中心，也就是原本的走法：
        // 呼叫端等的是「離中心夠近」，停在外面它會一直等下去。
        return chain
            .Then(_ => IsNear() || !(vnav.IsRunning() || vnav.IsSimpleMoveInProgress()))
            .ConditionalThen(_ => !IsNear(), _ =>
            {
                Svc.Log.Information($"[BOCCHI][魔路] 走到魔路旁 {destination} 停下時離中心還有 {Player.DistanceTo(center):f2}，改走向魔路中心");
                TravelIntent.Register(center, TravelSource.Aetheryte);
                vnav.PathfindAndMoveTo(center, false);
            });
    }

    private bool IsNear()
    {
        return Player.DistanceTo(center) <= AethernetData.DISTANCE;
    }

    /// <summary>找不到合格的點（或 vnavmesh 查詢失敗）時回傳中心本身，也就是原本的走法。</summary>
    public static Vector3 GetDestination(VNavmesh vnav, Vector3 center)
    {
        if (!Player.Available)
        {
            return center;
        }

        var from = Player.Position;
        var dir = new Vector2(from.X - center.X, from.Z - center.Z);
        if (dir.LengthSquared() < 0.25f)
        {
            return center;
        }

        dir = Vector2.Normalize(dir);
        foreach (var radius in Radii)
        {
            var raw = new Vector3(center.X + dir.X * radius, center.Y, center.Z + dir.Y * radius);
            Vector3? point;
            try
            {
                point = vnav.FindNearestPointOnMesh(raw, 1f, 3f);
            }
            catch (Exception ex)
            {
                Svc.Log.Information($"[BOCCHI][魔路] 查詢導航網格失敗，改走向魔路中心（原本的走法）：{ex.GetType().Name}：{ex.Message}");
                return center;
            }

            if (point is { } p && Vector3.Distance(p, center) <= MaxDistance)
            {
                Svc.Log.Debug($"[BOCCHI][魔路] 走到魔路旁 {p}（半徑 {radius}，離中心 {Vector3.Distance(p, center):f2}），不走向中心 {center}");
                return p;
            }
        }

        Svc.Log.Debug($"[BOCCHI][魔路] 魔路旁找不到離中心 {MaxDistance} 以內的網格點，改走向中心 {center}");
        return center;
    }

    public override TaskManagerConfiguration? Config()
    {
        return new TaskManagerConfiguration
        {
            TimeLimitMS = 180000,
        };
    }
}
