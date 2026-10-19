using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using BOCCHI.Automation;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Ocelot.Chain;
using Ocelot.IPC;

namespace BOCCHI.Modules.CriticalEncounters;

// 本檔契約：只在框架執行緒呼叫（趕路監看、動作鏈的任務）。
// 其他玩家只在挑點／到了的那一拍列舉物件表、當場抄下座標；不保存任何物件、包裝或指標。
// 一個實例＝一趟前往報名：只挑一次、到了只微調一次；到達前有人站上來不重挑（避免來回）。

/// <summary>前往緊急遭遇戰報名時，站到報名範圍內離其他玩家夠遠的隨機一點（規則見 <see cref="CeStandSpotRules"/>）。</summary>
public sealed class CeStandSpot(string origin, uint id, string name)
{
    private const string Tag = "[BOCCHI][CE站位]";

    // 傳送面板那一趟另開的動作鏈；名字以 ##watcher 結尾：停下會移動的自動化時跟主動作鏈一起中止。
    private const string TailQueue = "BOCCHI.CeStandSpot##watcher";

    // 那條動作鏈最多管這麼久（挑點前的趕路＋走到點上）。
    private const long TailLimitMs = 180_000;

    private bool nudgeDone;

    /// <summary>自己挑的點（已送出移動）；null＝還沒挑，或沒挑到、照原本的目的地。</summary>
    public Vector3? Spot { get; private set; }

    /// <summary>挑點；Picked 時已登記並送出移動，其他結果呼叫端照原本的目的地走。</summary>
    public CeSpotOutcome Begin(VNavmesh vnav, Vector3 center, float radius)
    {
        var player = Svc.Objects.LocalPlayer?.Position;
        List<Vector3> others;
        CeSpotResult result;
        try
        {
            others = OtherPlayers(center, CeStandSpotRules.OthersReach(radius));
            result = CeStandSpotRules.Pick(center, radius, others, Random.Shared, p => vnav.FindPointOnFloor(p, false, 0.5f));
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"{Head()} 挑點時出錯（{ex.GetType().Name}：{ex.Message}）：退回原本的目的地");
            return CeSpotOutcome.Failed;
        }

        var tail = $"範圍內其他玩家 {others.Count} 人、試了 {result.Tries} 次（查地面 {result.FloorQueries} 次、不合格 {result.FloorMisses}）；角色位置 {Pos(player)}";
        switch (result.Outcome)
        {
            case CeSpotOutcome.Picked:
                Spot = result.Point;
                TravelIntent.Register(result.Point, TravelSource.CriticalEncounterSpot);
                vnav.PathfindAndMoveTo(result.Point, false);
                Svc.Log.Information($"{Head()} 選了 {Pos(result.Point)}：離中心 {M(result.FromCenter)} 米（可站半徑 {M(CeStandSpotRules.StandRadius(radius))}＝報名半徑 {M(radius)} − 邊距 {M(CeStandSpotRules.EdgeMarginM)}）、"
                                    + $"最近玩家 {Nearest(result.NearestOther)}（至少 {M(CeStandSpotRules.ClearanceM)}）、{tail}");
                break;
            case CeSpotOutcome.NoArea:
                Svc.Log.Information($"{Head()} 報名半徑 {M(radius)} 米讀不到或太小（內縮 {M(CeStandSpotRules.EdgeMarginM)} 後要至少 {M(CeStandSpotRules.ClearanceM)}）：退回原本的目的地；角色位置 {Pos(player)}");
                break;
            default:
                Svc.Log.Information($"{Head()} 找不到離其他玩家都至少 {M(CeStandSpotRules.ClearanceM)} 米的空位：退回原本的目的地；{tail}");
                break;
        }

        return result.Outcome;
    }

    /// <summary>挑的點走不到、改回原本的目的地：之後不再挑、也不微調。</summary>
    public void GiveUp()
    {
        Spot = null;
    }

    /// <summary>導航停下時呼叫一次：停在自己挑的點附近、而且有人疊著才微調一次。回 true＝送出了微調的移動。</summary>
    public bool Arrive(VNavmesh vnav, Vector3 center, float radius)
    {
        if (nudgeDone || Spot is not { } spot || Svc.Objects.LocalPlayer?.Position is not { } player)
        {
            return false;
        }

        nudgeDone = true;
        var off = CeStandSpotRules.DistanceXZ(player, spot);
        if (off > CeStandSpotRules.ArrivedM)
        {
            Svc.Log.Debug($"{Head()} 停下時離挑的點 {M(off)} 米（被打斷或你接手了）：不微調；角色位置 {Pos(player)}");
            return false;
        }

        List<Vector3> others;
        CeSpotResult result;
        try
        {
            others = OtherPlayers(center, CeStandSpotRules.OthersReach(radius));
            result = CeStandSpotRules.Nudge(center, radius, player, others, Random.Shared, p => vnav.FindPointOnFloor(p, false, 0.5f));
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"{Head()} 到了，檢查有沒有人疊著時出錯（{ex.GetType().Name}：{ex.Message}）：不微調");
            return false;
        }

        switch (result.Outcome)
        {
            case CeSpotOutcome.Stayed:
                Svc.Log.Information($"{Head()} 到了：離中心 {M(result.FromCenter)} 米、最近玩家 {Nearest(result.NearestOther)}，不調整");
                return false;
            case CeSpotOutcome.Picked:
                Spot = result.Point;
                TravelIntent.Register(result.Point, TravelSource.CriticalEncounterSpot);
                vnav.PathfindAndMoveTo(result.Point, false);
                Svc.Log.Information($"{Head()} 到了，最近玩家 {Nearest(CeStandSpotRules.NearestXZ(player, others))}（不到 {M(CeStandSpotRules.ClearanceM)}）：微調一次到 {Pos(result.Point)}"
                                    + $"（移 {M(CeStandSpotRules.DistanceXZ(player, result.Point))} 米、離中心 {M(result.FromCenter)} 米、最近玩家 {Nearest(result.NearestOther)}、試了 {result.Tries} 次）");
                return true;
            default:
                Svc.Log.Information($"{Head()} 到了，最近玩家 {Nearest(CeStandSpotRules.NearestXZ(player, others))}（不到 {M(CeStandSpotRules.ClearanceM)}），"
                                    + $"周圍 {M(CeStandSpotRules.ClearanceM + CeStandSpotRules.NudgeRingM)} 米內找不到空位：留在原地（範圍內其他玩家 {others.Count} 人）");
                return false;
        }
    }

    /// <summary>
    /// 傳送面板「跑過去」「傳送後走過去」（跟團刷 CE 也走這條）送出走路後呼叫：另開一條動作鏈盯著這一趟，不佔主動作鏈（原本的等待與逾時照舊）。
    /// 這一趟仍是最新的趕路登記、導航還在走、離報名中心 <see cref="CeStandSpotRules.ApproachM"/> 內才挑點，停下時照 <see cref="Arrive"/> 微調；
    /// 別的移動接手、導航先停了（到了或你接手）、不在報名中、沒挑到就結束，原本送出的移動照走。
    /// </summary>
    public static void Follow(VNavmesh vnav, CriticalEncountersModule encounters, uint id, string origin)
    {
        var intent = TravelIntent.State;
        if (!intent.HasIntent || intent.Current.Source is not (TravelSource.TeleporterWalk or TravelSource.TeleporterAfterTeleport))
        {
            Svc.Log.Debug($"{Tag} 來源={origin} id={id} 找不到這一趟走路的登記：不挑站位點");
            return;
        }

        var walk = intent.Current;
        var queue = ChainManager.Get(TailQueue);
        queue.Abort();
        queue.Submit(() => Chain.Create(nameof(CeStandSpot)).Then(Tail(vnav, encounters, id, origin, walk)));
    }

    private static TaskManagerTask Tail(VNavmesh vnav, CriticalEncountersModule encounters, uint id, string origin, TravelIntentSnapshot walk)
    {
        CeStandSpot? spot = null;
        var current = walk;
        var deadline = Environment.TickCount64 + TailLimitMs;
        return new TaskManagerTask(() =>
        {
            if (!encounters.CriticalEncounters.TryGetValue(id, out var ce) || ce.State != DynamicEventState.Register || IsTower(ce))
            {
                return true;
            }

            if (!TravelIntent.State.IsCurrent(current))
            {
                Svc.Log.Debug($"{Tag} 來源={origin} id={id} 這一趟已被別的移動取代或停止：不再管站位");
                return true;
            }

            if (Environment.TickCount64 > deadline)
            {
                Svc.Log.Information($"{Tag} 來源={origin} id={id} name=「{ce.Name}」 {TailLimitMs / 1000} 秒內沒走完：不再管站位");
                return true;
            }

            if (Svc.Objects.LocalPlayer?.Position is not { } player)
            {
                return false;
            }

            var navigating = vnav.IsRunning() || vnav.IsSimpleMoveInProgress();
            if (spot == null)
            {
                if (!navigating)
                {
                    Svc.Log.Debug($"{Tag} 來源={origin} id={id} 還沒到 {M(CeStandSpotRules.ApproachM)} 米內導航就停了（到了或你接手了）：不挑站位點");
                    return true;
                }

                if (CeStandSpotRules.DistanceXZ(player, ce.Position) > CeStandSpotRules.ApproachM)
                {
                    return false;
                }

                spot = new CeStandSpot(origin, id, ce.Name);
                if (spot.Begin(vnav, ce.Position, ce.Radius) != CeSpotOutcome.Picked)
                {
                    return true;
                }

                current = TravelIntent.State.Current;
                return false;
            }

            if (navigating)
            {
                return false;
            }

            if (!Svc.Condition[ConditionFlag.InCombat])
            {
                spot.Arrive(vnav, ce.Position, ce.Radius);
            }

            return true;
        }, new TaskManagerConfiguration { TimeLimitMS = (int)TailLimitMs + 10_000 });
    }

    /// <summary>兩歧塔（DynamicEvent 種類 ≥ 4，主視窗也另外畫）：報名是站上平台，不套用隨機站位。</summary>
    public static bool IsTower(CriticalEncounterSnapshot encounter)
    {
        return encounter.EventType >= 4;
    }

    // 當拍列舉、只抄座標。
    private static List<Vector3> OtherPlayers(Vector3 center, float reach)
    {
        var list = new List<Vector3>();
        var self = Svc.Objects.LocalPlayer?.Address ?? nint.Zero;
        foreach (var obj in Svc.Objects)
        {
            if (obj.ObjectKind != ObjectKind.Player || obj.Address == self)
            {
                continue;
            }

            var position = obj.Position;
            if (CeStandSpotRules.DistanceXZ(position, center) <= reach)
            {
                list.Add(position);
            }
        }

        return list;
    }

    private string Head()
    {
        return $"{Tag} 來源={origin} id={id} name=「{name}」";
    }

    private static string M(float meters)
    {
        return meters.ToString("F1", CultureInfo.InvariantCulture);
    }

    private static string Nearest(float meters)
    {
        return float.IsPositiveInfinity(meters) ? "（附近沒有其他玩家）" : M(meters) + " 米";
    }

    private static string Pos(Vector3? position)
    {
        return position is { } p ? $"<{M(p.X)}, {M(p.Y)}, {M(p.Z)}>" : "?";
    }
}
