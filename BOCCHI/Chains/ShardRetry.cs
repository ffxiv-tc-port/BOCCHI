using System;
using System.Globalization;
using System.Numerics;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Pathfinding;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using Ocelot.IPC;

namespace BOCCHI.Chains;

public enum ShardRetryVerdict
{
    /// <summary>等角色停下後重新靠近最近的乙太之晶再試。</summary>
    Approach,

    /// <summary>最近的乙太之晶就是要傳送去的那個：不必傳送。</summary>
    NearestIsTarget,

    /// <summary>離最近的乙太之晶太遠：規劃傳送的前提（人在晶體旁）已經不成立。</summary>
    TooFar,
}

/// <summary>傳送鏈開始時 <see cref="AethernetData.DISTANCE"/> 米內沒有乙太之晶時的重新靠近規則。純邏輯；只在 framework 執行緒用。</summary>
public static class ShardRetryRules
{
    public const int MaxApproaches = 2;

    public const float ApproachLimitM = 40f;

    public const long StillWaitLimitMs = 20_000;

    public const long ArriveLimitMs = 20_000;

    /// <summary>從發現落空起算，超過就不再開始新的一次靠近。</summary>
    public const long BudgetMs = 60_000;

    public static ShardRetryVerdict Decide(Aethernet target, Aethernet nearest, float distance)
    {
        if (nearest == target)
        {
            return ShardRetryVerdict.NearestIsTarget;
        }

        return distance > ApproachLimitM ? ShardRetryVerdict.TooFar : ShardRetryVerdict.Approach;
    }
}

/// <summary>
/// 等角色停下（使用者不再自己移動）：相隔至少 <see cref="ManualMoveDetector.WindowMs"/> 的兩次位置，水平速度低於
/// <see cref="ManualMoveDetector.StillSpeedMax"/>、而且期間不忙，才算停下。純邏輯、不配置；只在 framework 執行緒用。
/// </summary>
public sealed class StillnessWatch
{
    private Vector3 anchor;

    private long anchorAt = -1;

    public void Reset()
    {
        anchorAt = -1;
    }

    /// <param name="busy">上下坐騎、詠唱、跳躍、讀圖、對話中。</param>
    public bool Observe(Vector3 player, bool busy, long now)
    {
        if (busy || anchorAt < 0)
        {
            anchor = player;
            anchorAt = busy ? -1 : now;
            return false;
        }

        var elapsed = now - anchorAt;
        if (elapsed < ManualMoveDetector.WindowMs)
        {
            return false;
        }

        var dx = player.X - anchor.X;
        var dz = player.Z - anchor.Z;
        var speed = MathF.Sqrt(dx * dx + dz * dz) / (elapsed / 1000f);
        anchor = player;
        anchorAt = now;
        return speed < ManualMoveDetector.StillSpeedMax;
    }
}

/// <summary>一條傳送鏈的重新靠近狀態：記錄原因與距離、計次、等停下、等走到。只在 framework 執行緒用（動作鏈的步驟裡）。</summary>
internal sealed class ShardApproach(Aethernet target)
{
    private readonly StillnessWatch still = new();

    private Aethernet nearest;

    private int attempts;

    private long startedAt;

    private long stillDeadline;

    private long arriveDeadline;

    private long stoppedSince;

    /// <summary>要靠近的中心：物件表裡找得到那座乙太之晶就用物件位置，否則用 BOCCHI 記的座標。</summary>
    public Vector3 Center { get; private set; }

    /// <summary>這一次等到角色停下了（可以送移動）。</summary>
    public bool MayMove { get; private set; }

    /// <returns>true＝要重新靠近；false＝照原流程往下（原因已寫進記錄）。</returns>
    public bool Begin()
    {
        startedAt = Environment.TickCount64;
        var player = Player.Position;
        var data = AethernetData.GetClosestTo(player);
        nearest = data.Aethernet;
        var found = TryFindShardObject(data.DataId, out var objectPosition);
        Center = found ? objectPosition : data.Position;
        var verdict = ShardRetryRules.Decide(target, nearest, Vector3.Distance(player, Center));
        var action = verdict switch
        {
            ShardRetryVerdict.Approach => $"等角色停下後重新靠近再試（最多 {ShardRetryRules.MaxApproaches} 次）",
            ShardRetryVerdict.NearestIsTarget => "最近的就是目的地，不重新靠近，照原流程往下（接著步行）",
            _ => $"超過 {ShardRetryRules.ApproachLimitM:F0} 米，不重新靠近，照原流程往下（接著步行）",
        };
        var objectText = found ? Vector3.Distance(player, objectPosition).ToString("F2", CultureInfo.InvariantCulture) : "?（物件表裡沒有）";
        Svc.Log.Information(string.Create(CultureInfo.InvariantCulture,
            $"[BOCCHI][傳送] 要傳送到 {target}，但 {AethernetData.DISTANCE:F1} 米內沒有乙太之晶：最近的是 {nearest}，離物件 {objectText} 米、離 BOCCHI 記的座標 {Vector3.Distance(player, data.Position):F2} 米；"
            + $"{action}。角色位置 {player:F1}"));
        return verdict == ShardRetryVerdict.Approach;
    }

    public bool ShouldApproach()
    {
        if (IsNear() || attempts >= ShardRetryRules.MaxApproaches || Environment.TickCount64 - startedAt > ShardRetryRules.BudgetMs)
        {
            return false;
        }

        attempts++;
        MayMove = false;
        stillDeadline = 0;
        arriveDeadline = 0;
        still.Reset();
        Svc.Log.Debug(string.Create(CultureInfo.InvariantCulture, $"[BOCCHI][傳送] 第 {attempts} 次重新靠近 {nearest}：離中心 {Player.DistanceTo(Center):F2} 米"));
        return true;
    }

    public bool WaitUntilStill()
    {
        var now = Environment.TickCount64;
        if (stillDeadline == 0)
        {
            stillDeadline = now + ShardRetryRules.StillWaitLimitMs;
        }

        var player = Svc.Objects.LocalPlayer;
        if (player != null && still.Observe(player.Position, IsBusy(), now))
        {
            MayMove = true;
            return true;
        }

        if (now < stillDeadline)
        {
            return false;
        }

        Svc.Log.Information($"[BOCCHI][傳送] 第 {attempts} 次重新靠近 {nearest}：等角色停下超過 {ShardRetryRules.StillWaitLimitMs / 1000} 秒（還在移動或對話中），這次不靠近");
        return true;
    }

    public bool WaitArrived(VNavmesh vnav)
    {
        if (!MayMove || Player.DistanceTo(Center) <= AethernetData.DISTANCE)
        {
            return true;
        }

        var now = Environment.TickCount64;
        if (arriveDeadline == 0)
        {
            arriveDeadline = now + ShardRetryRules.ArriveLimitMs;
            stoppedSince = 0;
        }

        if (now >= arriveDeadline)
        {
            return true;
        }

        if (vnav.IsRunning() || vnav.IsSimpleMoveInProgress())
        {
            stoppedSince = 0;
            return false;
        }

        stoppedSince = stoppedSince == 0 ? now : stoppedSince;
        return now - stoppedSince >= ArrivalGuard.SettleMs;
    }

    /// <returns>true＝現在在乙太之晶旁，照常傳送。</returns>
    public bool Finish()
    {
        var near = IsNear();
        if (near)
        {
            Svc.Log.Information(string.Create(CultureInfo.InvariantCulture, $"[BOCCHI][傳送] 重新靠近 {attempts} 次後在 {nearest} 的 {AethernetData.DISTANCE:F1} 米內，照常傳送到 {target}"));
        }
        else
        {
            Svc.Log.Information(string.Create(CultureInfo.InvariantCulture,
                $"[BOCCHI][傳送] 重新靠近 {attempts} 次仍不在乙太之晶 {AethernetData.DISTANCE:F1} 米內（離 {nearest} 中心 {Player.DistanceTo(Center):F2} 米）：不傳送到 {target}，照原流程往下（接著步行）"));
        }

        return near;
    }

    private static bool IsNear()
    {
        return ZoneData.GetNearbyAethernetShards(AethernetData.DISTANCE).Count > 0;
    }

    private static bool TryFindShardObject(uint dataId, out Vector3 position)
    {
        foreach (var obj in Svc.Objects)
        {
            if (obj.ObjectKind == ObjectKind.EventObj && obj.BaseId == dataId)
            {
                position = obj.Position;
                return true;
            }
        }

        position = default;
        return false;
    }

    private static bool IsBusy()
    {
        var condition = Svc.Condition;
        return condition[ConditionFlag.Mounting] || condition[ConditionFlag.Mounting71] || condition[ConditionFlag.MountOrOrnamentTransition]
               || condition[ConditionFlag.Casting] || condition[ConditionFlag.Casting87]
               || condition[ConditionFlag.Jumping] || condition[ConditionFlag.Jumping61]
               || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]
               || condition[ConditionFlag.Occupied] || condition[ConditionFlag.Occupied30] || condition[ConditionFlag.Occupied33]
               || condition[ConditionFlag.Occupied38] || condition[ConditionFlag.Occupied39]
               || condition[ConditionFlag.OccupiedInEvent] || condition[ConditionFlag.OccupiedInQuestEvent] || condition[ConditionFlag.OccupiedInCutSceneEvent];
    }
}
