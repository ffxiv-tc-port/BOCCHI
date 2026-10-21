using System;
using System.Collections.Generic;
using System.Numerics;
using BOCCHI.ActionHelpers;
using BOCCHI.Automation;
using BOCCHI.Enums;
using BOCCHI.ItemHelpers;
using BOCCHI.Modules.Carrots;
using BOCCHI.Pathfinding;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;
using Ocelot.IPC;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace BOCCHI.Modules.Treasure;

/// <summary>
/// 寶箱巡迴「順路挖幸運胡蘿蔔」：巡迴走路途中看到幸運胡蘿蔔、距離在上限內就插一步先去挖
/// （走過去、下坐騎、用一個幸運胡蘿蔔、開出現的黃金財寶箱），挖完或放棄就接回原本那一步。
/// 挖完的判準＝那個胡蘿蔔物件消失（存 GameObjectId、每拍重查），不看互動回傳值；同一根只試一次。
/// 框架執行緒專用；不保存任何物件包裝或原生指標。
/// </summary>
public sealed class CarrotDetour
{
    private const string Tag = "[BOCCHI][順路胡蘿蔔]";

    private const long ScanIntervalMs = 500;

    private const int MemoryCapacity = 64;

    private const float ChestSearchRadius = 30f;

    private readonly SilverChestMemory attempted = new(MemoryCapacity);

    // 每根上次寫進記錄的「不插隊」原因：原因沒變就不再寫。
    private readonly Dictionary<ulong, CarrotVerdict> reported = [];

    private long nextScanAt;

    private bool active;

    private ulong targetId;

    private Vector3 targetPosition;

    private long startedAt;

    private int walkLimitMs;

    private DigProgress? dig;

    // 每挖一根建一個；被中止的舊動作鏈之後才寫進來的收尾落在已經丟掉的那一個上。
    private sealed class DigProgress(int carrotsBefore)
    {
        public readonly int CarrotsBefore = carrotsBefore;

        public string Stage = "下坐騎";

        public bool Used;

        public int UseAttempts;

        public long NextUseAt;

        public readonly BunnyChestOpener Chest = new();

        public bool Cancelled;

        public long FinishedAt;
    }

    public bool IsActive
    {
        get => active;
    }

    /// <summary>從頭開始巡迴時清掉「試過」的紀錄；續跑沿用（剛放棄的那根不再試）。</summary>
    public void Clear()
    {
        Drop();
        attempted.Clear();
        reported.Clear();
    }

    /// <summary>巡迴停下（停止、暫停、換區）時呼叫：放棄進行中的那根。</summary>
    public void Stop()
    {
        if (active)
        {
            Svc.Log.Information($"{Tag} 放棄：巡迴停下了（{Seconds(Environment.TickCount64):F1} 秒，停在「{Stage}」）；位置 {Format(targetPosition)}");
        }

        Drop();
    }

    /// <param name="stepNumber">插在第幾步前面（記錄用，從 1 起算）。</param>
    public bool TryPick(TreasureConfig config, int stepNumber, PathfinderStepType stepType, out ulong objectId, out Vector3 position)
    {
        objectId = 0;
        position = default;
        if (active || !config.ShouldHuntDigCarrots)
        {
            return false;
        }

        var now = Environment.TickCount64;
        if (now < nextScanAt)
        {
            return false;
        }

        nextScanAt = now + ScanIntervalMs;
        var player = Svc.Objects.LocalPlayer;
        if (player == null || player.IsDead)
        {
            return false;
        }

        var playerPosition = player.Position;
        var inCombat = Svc.Condition[ConditionFlag.InCombat];
        var carrots = -1;
        var found = false;
        var bestDistance = float.MaxValue;
        foreach (var obj in Svc.Objects)
        {
            if (obj.ObjectKind != ObjectKind.EventObj || obj.BaseId != (uint)OccultObjectType.Carrot || obj.IsDead)
            {
                continue;
            }

            var id = obj.GameObjectId;
            var at = obj.Position;
            if (attempted.Contains(id, at))
            {
                continue;
            }

            if (carrots < 0)
            {
                carrots = Items.FortuneCarrot.Count();
            }

            var distance = Vector3.Distance(playerPosition, at);
            var verdict = CarrotDetourRules.Evaluate(distance, config.HuntCarrotMaxDistance, carrots, inCombat);
            if (verdict != CarrotVerdict.Go)
            {
                Report(id, at, verdict, distance, config.HuntCarrotMaxDistance);
                continue;
            }

            if (distance < bestDistance)
            {
                found = true;
                bestDistance = distance;
                objectId = id;
                position = at;
            }
        }

        if (!found)
        {
            return false;
        }

        attempted.Add(objectId, position);
        reported.Remove(objectId);
        active = true;
        dig = null;
        targetId = objectId;
        targetPosition = position;
        startedAt = now;
        walkLimitMs = CarrotDetourRules.WalkTimeLimitMs(bestDistance);
        Svc.Log.Information($"{Tag} 看到幸運胡蘿蔔，插隊去挖：位置 {Format(position)}，距離 {bestDistance:F1} 米（上限 {config.HuntCarrotMaxDistance}），"
                            + $"插在第 {stepNumber} 步（{stepType}）前面，挖完接回；身上幸運胡蘿蔔 {carrots} 個；走過去上限 {walkLimitMs / 1000} 秒");
        return true;
    }

    /// <summary>DigCarrot 那一步每一拍呼叫（步驟處理器沒在跑時）；送出挖的動作鏈時用 <paramref name="processor"/>。</summary>
    public CarrotStepAction Step(PathfinderStep step, ChainQueue processor, VNavmesh vnav)
    {
        if (!active || step.ObjectId != targetId)
        {
            return CarrotStepAction.Done;
        }

        var player = Svc.Objects.LocalPlayer;
        if (player == null)
        {
            return CarrotStepAction.Hold;
        }

        var now = Environment.TickCount64;
        var present = IsCarrotPresent(targetId);
        var distance = Vector3.Distance(player.Position, targetPosition);
        CarrotOutcome outcome;
        if (dig == null)
        {
            var action = CarrotDetourRules.Approach(present, distance, now - startedAt, walkLimitMs, out outcome);
            if (action == CarrotStepAction.Hold)
            {
                StartDig(processor, vnav, distance, now);
            }
            else if (action == CarrotStepAction.Done)
            {
                Finish(outcome, distance, now);
            }

            return action;
        }

        // 步驟處理器空下來才會呼叫到這裡：挖的動作鏈已經跑完或被中止。
        if (dig.FinishedAt == 0)
        {
            dig.FinishedAt = now;
        }

        var after = CarrotDetourRules.AfterDig(present, dig.Used, now - dig.FinishedAt, out outcome);
        if (after == CarrotStepAction.Done)
        {
            Finish(outcome, distance, now);
        }

        return after;
    }

    /// <summary>那一步走不到，巡迴已經移除它。</summary>
    public void Unreachable(string what)
    {
        if (active)
        {
            Svc.Log.Information($"{Tag} 放棄：{what}（{Seconds(Environment.TickCount64):F1} 秒）；位置 {Format(targetPosition)}，接回巡迴");
        }

        Drop();
    }

    private void Drop()
    {
        active = false;
        dig = null;
    }

    private void StartDig(ChainQueue processor, VNavmesh vnav, float distance, long now)
    {
        OwnPathfind.CancelIfOnlyOwn(vnav, OwnMoveDestinations.HasCurrent(TravelIntent.State));
        vnav.Stop();
        var carrots = Items.FortuneCarrot.Count();
        var progress = new DigProgress(carrots);
        dig = progress;
        processor.SubmitFront(CreateDigChain(progress, vnav));
        Svc.Log.Information($"{Tag} 到了胡蘿蔔旁（距離 {distance:F1} 米，走了 {(now - startedAt) / 1000f:F1} 秒）：下坐騎、用幸運胡蘿蔔（身上 {carrots} 個）、開黃金財寶箱");
    }

    private void Finish(CarrotOutcome outcome, float distance, long now)
    {
        var seconds = Seconds(now);
        var progress = dig;
        var message = outcome switch
        {
            CarrotOutcome.Dug => $"挖到：胡蘿蔔已消失（幸運胡蘿蔔 {Carrots(progress)}；黃金財寶箱：{Chest(progress)}；{seconds:F1} 秒）",
            CarrotOutcome.Gone when progress == null => $"放棄：還沒走到胡蘿蔔就不見了（多半被別人挖走；人離它 {distance:F1} 米，{seconds:F1} 秒）",
            CarrotOutcome.Gone => $"放棄：胡蘿蔔不見了，但沒看到用掉幸運胡蘿蔔（停在「{Stage}」，{seconds:F1} 秒）",
            CarrotOutcome.StillThere => $"用了幸運胡蘿蔔（{Carrots(progress)}），等了 {CarrotDetourRules.SettleMs / 1000} 秒胡蘿蔔還在；黃金財寶箱：{Chest(progress)}；這一根不再試（{seconds:F1} 秒）",
            CarrotOutcome.DigFailed => $"放棄：挖的步驟沒做完（停在「{Stage}」{(progress?.Cancelled == true ? "，逾時或中止" : string.Empty)}，用道具試了 {progress?.UseAttempts ?? 0} 次），胡蘿蔔還在；這一根不再試（{seconds:F1} 秒）",
            CarrotOutcome.WalkTimeout => $"放棄：走了 {seconds:F1} 秒還沒到（上限 {walkLimitMs / 1000} 秒，人離胡蘿蔔 {distance:F1} 米）",
            _ => $"結束（{outcome}，{seconds:F1} 秒）",
        };
        Svc.Log.Information($"{Tag} {message}；位置 {Format(targetPosition)}，接回巡迴");
        Drop();
    }

    private string Stage
    {
        get => dig?.Stage ?? "走過去";
    }

    private float Seconds(long now)
    {
        return (now - startedAt) / 1000f;
    }

    private static string Carrots(DigProgress? progress)
    {
        return progress == null ? "?" : $"{progress.CarrotsBefore}→{Items.FortuneCarrot.Count()}";
    }

    private static string Chest(DigProgress? progress)
    {
        var chest = progress?.Chest;
        if (chest is not { Seen: true })
        {
            return "沒看到";
        }

        if (chest.EventSeen)
        {
            return chest.Gone ? "開了" : "開過（寶箱還在）";
        }

        return chest.Gone ? $"互動 {chest.Interacts} 次後不見了" : $"互動 {chest.Interacts} 次沒有開箱事件";
    }

    private void Report(ulong id, Vector3 position, CarrotVerdict verdict, float distance, int maxDistance)
    {
        if (reported.TryGetValue(id, out var last) && last == verdict)
        {
            return;
        }

        if (reported.Count >= MemoryCapacity)
        {
            reported.Clear();
        }

        reported[id] = verdict;
        var why = verdict switch
        {
            CarrotVerdict.NoCarrot => "身上沒有幸運胡蘿蔔",
            CarrotVerdict.TooFar => $"距離 {distance:F1} 米超過上限 {maxDistance}",
            CarrotVerdict.InCombat => "戰鬥中",
            _ => verdict.ToString(),
        };
        Svc.Log.Information($"{Tag} 看到幸運胡蘿蔔（位置 {Format(position)}，距離 {distance:F1} 米），不插隊：{why}；之後條件變了會再看");
    }

    // 只在當幀查物件表、讀完就放手。
    private static bool IsCarrotPresent(ulong id)
    {
        var current = Svc.Objects.SearchById(id);
        return current is { ObjectKind: ObjectKind.EventObj, IsDead: false } && current.BaseId == (uint)OccultObjectType.Carrot;
    }

    private static Func<Chain> CreateDigChain(DigProgress progress, VNavmesh vnav)
    {
        return () => Chain.Create("Treasure:Carrot")
            .Then(_ => { Actions.TryUnmount(); })
            .Then(new TaskManagerTask(() => !Svc.Condition[ConditionFlag.Mounted], "Carrot.Unmounted", new TaskManagerConfiguration { TimeLimitMS = 5_000 }))
            .Wait(500)
            .Then(_ => { progress.Stage = "用幸運胡蘿蔔"; })
            .BreakIf(() => Items.FortuneCarrot.Count() <= 0)
            .Then(new TaskManagerTask(() => UseCarrot(progress), "Carrot.Use", new TaskManagerConfiguration { TimeLimitMS = 6_000 }))
            .Then(new TaskManagerTask(() => Svc.Objects.LocalPlayer?.IsCasting != true, "Carrot.CastEnd", new TaskManagerConfiguration { TimeLimitMS = 5_000 }))
            .Then(_ => { progress.Stage = "等黃金財寶箱出現"; })
            .Then(new TaskManagerTask(() => progress.Chest.TryFind(ChestSearchRadius), "Carrot.WaitChest", new TaskManagerConfiguration { TimeLimitMS = 15_000 }))
            .Then(_ => { progress.Stage = "開黃金財寶箱"; })
            .Then(new TaskManagerTask(() => progress.Chest.Step(vnav), "Carrot.OpenChest", new TaskManagerConfiguration { TimeLimitMS = 20_000 }))
            .OnCancel(() => progress.Cancelled = true);
    }

    // 看到詠唱或幸運胡蘿蔔少了一個就算用了；沒反應每 1.5 秒再用一次。
    private static bool UseCarrot(DigProgress progress)
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (player.IsCasting || Items.FortuneCarrot.Count() < progress.CarrotsBefore)
        {
            progress.Used = true;
            return true;
        }

        var now = Environment.TickCount64;
        if (now < progress.NextUseAt)
        {
            return false;
        }

        progress.NextUseAt = now + 1_500;
        progress.UseAttempts++;
        OwnInteraction.Note(now);
        Items.FortuneCarrot.Use();
        return false;
    }

    private static string Format(Vector3 position)
    {
        return $"({position.X:F1}, {position.Y:F1}, {position.Z:F1})";
    }
}
