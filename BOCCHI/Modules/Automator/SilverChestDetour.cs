using System;
using System.Collections.Generic;
using System.Numerics;
using BOCCHI.Automation;
using BOCCHI.Chains;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.StateManager;
using BOCCHI.Modules.Treasure;
using BOCCHI.Pathfinding;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Ocelot.Chain;
using Ocelot.IPC;
using NativeTreasure = FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure;

namespace BOCCHI.Modules.Automator;

/// <summary>
/// 非法模式「順路開白銀寶箱」：兩個活動之間（下一個活動還沒到場，或沒有活動可選），寶箱模組已經看到、
/// 離得夠近的白銀寶箱就先去開，開完或放棄再照原本的下一步。不為了找寶箱繞路，同一個寶箱只試一次。
/// 框架執行緒專用；停止非法模式（含全部停止）與換區時由 <see cref="Automator.Refresh"/> 呼叫 <see cref="Reset"/>。
/// </summary>
public sealed class SilverChestDetour
{
    private const string ChainName = "Illegal:SilverChest";

    private const string Tag = "[BOCCHI][順路銀寶箱]";

    // 開箱步驟要站在 2 米內，走到 1.5 米內才停。
    private const float ArriveDistance = 1.5f;

    private const int OpenTimeLimitMs = 10_000;

    // 每一步各有上限；這個是兜底（走路上限＋開箱上限＋5 秒），動作鏈沒有收尾也一定會結束。
    private long overallTimeLimitMs;

    private const long TravelCheckIntervalMs = 250;

    private const int MemoryCapacity = 256;

    private enum ChestState
    {
        Gone,
        Opened,
        StillThere,
    }

    private readonly SilverChestMemory attempted = new(MemoryCapacity);

    // 每個寶箱上次寫進 log 的「不去」原因：原因沒變就不再寫。
    private readonly Dictionary<ulong, SilverChestVerdict> reported = [];

    private TreasureModule? treasure;

    private bool treasureResolved;

    private CriticalEncountersModule? criticalEncounters;

    private bool criticalEncountersResolved;

    private bool running;

    // 每次停止 +1：被中止的舊動作鏈之後才跑到的收尾一律不理。
    private int generation;

    private long startedAt;

    private long nextTravelCheckAt;

    private ulong targetId;

    private Vector3 targetPosition;

    // 這一趟走路的重走次數；走路那段沒跑完時是 -1。
    private int rewalks;

    // 這一趟走路讓位給使用者操作的累計：走路段與兜底時限都補回這段時間。
    private WalkYieldLedger walkYield = new();

    /// <summary>冪等：放棄進行中的開箱（動作鏈與導航由呼叫端收），清掉「試過」的紀錄。</summary>
    public void Reset(string reason)
    {
        if (running)
        {
            Svc.Log.Information($"{Tag} 去開白銀寶箱途中停止（{reason}）：放棄這個寶箱（位置 {Format(targetPosition)}）。");
        }

        running = false;
        generation++;
        nextTravelCheckAt = 0;
        attempted.Clear();
        reported.Clear();
    }

    /// <returns>true＝開箱進行中，非法模式這一幀不做別的。</returns>
    public bool Watch(AutomatorModule module)
    {
        if (!running)
        {
            return false;
        }

        if (Environment.TickCount64 - startedAt <= overallTimeLimitMs + walkYield.TotalMs)
        {
            return true;
        }

        running = false;
        generation++;
        var navigation = module.Plugin.Automation.AbortIllegalModeTravel();
        Svc.Log.Information($"{Tag} 開白銀寶箱超過 {overallTimeLimitMs / 1000} 秒（另補回讓位 {walkYield.TotalMs / 1000f:F1} 秒）還沒結束：放棄這個寶箱（位置 {Format(targetPosition)}），回到原本的下一步；導航：{navigation}");
        return false;
    }

    /// <param name="activity">還沒到場的下一個活動；null＝目前沒有活動可選。</param>
    /// <param name="chainActive">有動作鏈在跑：只在前往活動的走路途中打斷。</param>
    /// <returns>true＝這一幀開始去開寶箱。</returns>
    public bool TryStart(AutomatorModule module, StateManagerModule states, Activity? activity, Lifestream lifestream, bool chainActive)
    {
        if (running || !module.Config.ShouldPrioritizeSilverChests)
        {
            return false;
        }

        if (activity != null && activity.state is not (ActivityState.Idle or ActivityState.Pathfinding))
        {
            return false;
        }

        if (states.GetState() != State.Idle)
        {
            return false;
        }

        var player = Svc.Objects.LocalPlayer;
        if (player == null || player.IsDead)
        {
            return false;
        }

        var playerPosition = player.Position;
        var now = Environment.TickCount64;
        Vector3? destination = activity?.Destination;
        var radius = activity?.DestinationRadius ?? 0f;
        var shardLeg = false;
        if (chainActive)
        {
            if (activity is not { state: ActivityState.Pathfinding } || now < nextTravelCheckAt)
            {
                return false;
            }

            nextTravelCheckAt = now + TravelCheckIntervalMs;
            if (!TryGetWalkingLeg(playerPosition, out var leg))
            {
                return false;
            }

            // 先走到城內乙太之晶再傳送的那一段：順不順路以乙太之晶為準。
            if (leg.Source == TravelSource.IllegalModeShard)
            {
                destination = leg.Destination;
                radius = 0f;
                shardLeg = true;
            }
        }

        if (!TryGetTreasure(module, out var source))
        {
            return false;
        }

        var config = module.Config;
        BOCCHI.Modules.Treasure.Treasure? best = null;
        var bestScore = float.MaxValue;
        var bestDistance = 0f;
        var bestDetour = 0f;
        foreach (var chest in source.Treasures)
        {
            if (chest.GetTreasureType() != TreasureType.Silver)
            {
                continue;
            }

            var position = chest.LastKnownPosition;
            if (attempted.Contains(chest.Id, position))
            {
                continue;
            }

            if (chest.IsOpened)
            {
                Report(chest.Id, position, SilverChestVerdict.AlreadyOpened, Vector3.Distance(playerPosition, position), 0f, config, activity, shardLeg);
                continue;
            }

            var verdict = SilverChestDetourRules.Evaluate(playerPosition, position, config.SilverChestMaxDistance, config.SilverChestMaxDetour, destination, radius, out var distance, out var detour);
            if (verdict != SilverChestVerdict.Go)
            {
                Report(chest.Id, position, verdict, distance, detour, config, activity, shardLeg);
                continue;
            }

            var score = destination == null ? distance : detour;
            if (score < bestScore)
            {
                best = chest;
                bestScore = score;
                bestDistance = distance;
                bestDetour = detour;
            }
        }

        if (best == null)
        {
            return false;
        }

        var bestPosition = best.LastKnownPosition;
        var soonest = SoonestEncounterSecondsLeft(module);
        if (SilverChestDetourRules.EncounterBlocks(soonest))
        {
            Report(best.Id, bestPosition, SilverChestVerdict.EncounterClosing, bestDistance, soonest ?? -1, config, activity, shardLeg);
            return false;
        }

        if (Automator.IsCastingOrBusy() || ZoneData.IsInForkedTowerNoAlloc(player) || Automator.IsLifestreamBusy(lifestream))
        {
            return false;
        }

        if (activity != null && activity.IsTravelYielding(now))
        {
            Report(best.Id, bestPosition, SilverChestVerdict.PlayerSteering, bestDistance, bestDetour, config, activity, shardLeg);
            return false;
        }

        var navigation = chainActive ? module.Plugin.Automation.AbortIllegalModeTravel() : "沒有進行中的趕路";
        Start(module, best.Id, bestPosition, bestDistance, now);
        var route = activity == null
            ? "目前沒有可參加的活動"
            : $"比直接走到{Describe(activity, shardLeg)}多走 {bestDetour:F1} 米（上限 {config.SilverChestMaxDetour}）";
        Svc.Log.Information($"{Tag} 去開白銀寶箱：位置 {Format(bestPosition)}，距離 {bestDistance:F1} 米（上限 {config.SilverChestMaxDistance}），{route}；導航：{navigation}");
        return true;
    }

    private void Start(AutomatorModule module, ulong id, Vector3 position, float distance, long now)
    {
        var walkLimitMs = SilverChestDetourRules.WalkTimeLimitMs(distance);
        overallTimeLimitMs = walkLimitMs + OpenTimeLimitMs + 5_000;
        attempted.Add(id, position);
        reported.Remove(id);
        running = true;
        startedAt = now;
        targetId = id;
        targetPosition = position;
        rewalks = -1;
        var ledger = new WalkYieldLedger();
        walkYield = ledger;

        var token = generation;
        Plugin.Chain.Submit(() => Chain.Create(ChainName)
            .ConditionalThen(_ => module.Config.ShouldToggleAiProvider && !Svc.Condition[ConditionFlag.InCombat], _ => module.SetAi(false, "順路去開白銀寶箱"))
            .Then(ChainHelper.PathfindToAndWait(position, ArriveDistance, TravelSource.SilverChest, guard => NoteWalk(token, guard), () => module.Config.YieldToManualMovement, ledger),
                ledger.TimeLimit(walkLimitMs, $"順路開白銀寶箱走向 {Format(position)}"))
            .Then(TreasureHunt.CreateOpenChain(id, position), new TaskManagerConfiguration { TimeLimitMS = OpenTimeLimitMs })
            .OnComplete(() => Finish(token, true))
            .OnCancel(() => Finish(token, false)));
    }

    private void Finish(int token, bool completed)
    {
        if (token != generation || !running)
        {
            return;
        }

        running = false;
        var seconds = (Environment.TickCount64 - startedAt) / 1000f;
        if (!completed)
        {
            Svc.Log.Information($"{Tag} 放棄：去開白銀寶箱沒有走完（逾時或步驟失敗，{seconds:F1} 秒），這個寶箱不再嘗試（位置 {Format(targetPosition)}）。");
            return;
        }

        var playerDistance = Svc.Objects.LocalPlayer is { } player ? Vector3.Distance(player.Position, targetPosition) : -1f;
        var state = Inspect(targetId);
        var walked = rewalks < 0 ? "走路沒跑完" : $"重走 {rewalks} 次";
        var message = SilverChestDetourRules.Judge(state == ChestState.Gone, state == ChestState.Opened, playerDistance) switch
        {
            SilverChestOutcome.Opened => $"開到了：白銀寶箱已開啟（{seconds:F1} 秒）",
            SilverChestOutcome.Gone => $"開到了或已被開走：白銀寶箱已經不在（{seconds:F1} 秒，人離寶箱 {playerDistance:F1} 米）",
            SilverChestOutcome.OpenedByOthers => $"沒走到寶箱旁，白銀寶箱已被開啟（多半是別人開的；{seconds:F1} 秒，人離寶箱 {playerDistance:F1} 米，{walked}）",
            SilverChestOutcome.GoneUnreached => $"沒走到寶箱旁，白銀寶箱已經不在（{seconds:F1} 秒，人離寶箱 {playerDistance:F1} 米，{walked}）",
            SilverChestOutcome.NotReached => $"放棄：沒走到白銀寶箱旁、沒有互動（{seconds:F1} 秒，人離寶箱 {playerDistance:F1} 米，{walked}），這個寶箱不再嘗試",
            _ => $"放棄：互動後白銀寶箱還在（{seconds:F1} 秒，人離寶箱 {playerDistance:F1} 米），這個寶箱不再嘗試",
        };
        Svc.Log.Information($"{Tag} {message}（位置 {Format(targetPosition)}）。");
    }

    private void NoteWalk(int token, ArrivalGuard guard)
    {
        if (token == generation)
        {
            rewalks = guard.Rewalks;
        }
    }

    // 只在當幀查物件表、當幀讀完旗標，不保存物件。
    private static unsafe ChestState Inspect(ulong id)
    {
        var current = Svc.Objects.SearchById(id);
        if (current == null || current.ObjectKind != ObjectKind.Treasure)
        {
            return ChestState.Gone;
        }

        if (!current.IsTargetable)
        {
            return ChestState.Opened;
        }

        var native = (NativeTreasure*)current.Address;
        if (native == null)
        {
            return ChestState.Gone;
        }

        return native->Flags.HasFlag(NativeTreasure.TreasureFlags.Opened) ? ChestState.Opened : ChestState.StillThere;
    }

    // 只有非法模式前往活動的走路段可以打斷；返回、傳送、緊急遭遇戰場內站位都不算。
    private static bool TryGetWalkingLeg(Vector3 playerPosition, out TravelIntentSnapshot leg)
    {
        if (!TravelIntent.State.TryGetActive(Svc.ClientState.TerritoryType, playerPosition, Environment.TickCount64, out leg))
        {
            return false;
        }

        return leg.Source is TravelSource.IllegalModeFate or TravelSource.IllegalModeCriticalEncounter or TravelSource.IllegalModeShard;
    }

    private static string Describe(Activity? activity, bool shardLeg)
    {
        var name = activity?.GetName() ?? "?";
        return shardLeg ? $"城內乙太之晶（之後傳送前往「{name}」）" : $"「{name}」";
    }

    private bool TryGetTreasure(AutomatorModule module, out TreasureModule source)
    {
        if (!treasureResolved)
        {
            treasureResolved = true;
            module.TryGetModule(out treasure);
        }

        source = treasure!;
        return treasure is { IsEnabled: true };
    }

    private long? SoonestEncounterSecondsLeft(AutomatorModule module)
    {
        var config = module.Config;
        if (!config.ShouldDoCriticalEncounters)
        {
            return null;
        }

        if (!criticalEncountersResolved)
        {
            criticalEncountersResolved = true;
            module.TryGetModule(out criticalEncounters);
        }

        if (criticalEncounters == null)
        {
            return null;
        }

        var enabled = config.CriticalEncountersMap;
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long? soonest = null;
        foreach (var encounter in criticalEncounters.CriticalEncounters.Values)
        {
            if (encounter.State != DynamicEventState.Register || !enabled.TryGetValue(encounter.DynamicEventId, out var on) || !on)
            {
                continue;
            }

            var left = SilverChestDetourRules.SecondsLeft(encounter.StartTimestamp, nowUnix);
            if (soonest == null || left < soonest)
            {
                soonest = left;
            }
        }

        return soonest;
    }

    private void Report(ulong id, Vector3 position, SilverChestVerdict verdict, float distance, float detourOrSeconds, AutomatorConfig config, Activity? activity, bool shardLeg)
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
            SilverChestVerdict.Arriving => $"已經接近{Describe(activity, shardLeg)}，不中途轉去開",
            SilverChestVerdict.TooFar => $"距離 {distance:F1} 米超過上限 {config.SilverChestMaxDistance}",
            SilverChestVerdict.DetourTooLong => $"比直接走到{Describe(activity, shardLeg)}多走 {detourOrSeconds:F1} 米，超過上限 {config.SilverChestMaxDetour}",
            SilverChestVerdict.EncounterClosing => detourOrSeconds < 0
                ? "有要參加的緊急遭遇戰正在報名，但讀不到剩餘時間或已過截止"
                : $"有要參加的緊急遭遇戰報名只剩 {detourOrSeconds:F0} 秒（少於 {SilverChestDetourRules.MinEncounterSecondsLeft}）",
            SilverChestVerdict.AlreadyOpened => "寶箱已經開了（你或別人剛開的），不再去互動",
            SilverChestVerdict.PlayerSteering => $"前往{Describe(activity, shardLeg)}的趕路正在讓位給你操作，先不插隊；接續趕路後再看",
            _ => verdict.ToString(),
        };
        var tail = verdict == SilverChestVerdict.AlreadyOpened ? "。" : "；之後條件變了會再看。";
        Svc.Log.Information($"{Tag} 不去開白銀寶箱（位置 {Format(position)}，距離 {distance:F1} 米）：{why}{tail}");
    }

    private static string Format(Vector3 position)
    {
        return $"({position.X:F1}, {position.Y:F1}, {position.Z:F1})";
    }
}
