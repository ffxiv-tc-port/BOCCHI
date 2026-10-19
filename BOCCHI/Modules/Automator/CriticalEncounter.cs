using BOCCHI.ActionHelpers;
using BOCCHI.Automation;
using BOCCHI.Data;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.StateManager;
using BOCCHI.Pathfinding;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Ocelot.Chain;
using Ocelot.IPC;
using System;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace BOCCHI.Modules.Automator;

public class CriticalEncounter : Activity
{
    private readonly CriticalEncountersModule source;

    // 當幀清單裡沒有這一場（讀取中清單被清空）時用最後一次看到的值，不擲 KeyNotFoundException。
    private CriticalEncounterSnapshot lastSeen;

    private bool TryGetEncounter(out CriticalEncounterSnapshot encounter)
    {
        if (source.CriticalEncounters.TryGetValue(data.Id, out encounter))
        {
            lastSeen = encounter;
            return true;
        }

        encounter = lastSeen;
        return false;
    }

    private CriticalEncounterSnapshot Encounter
    {
        get
        {
            TryGetEncounter(out var encounter);
            return encounter;
        }
    }

    private bool finalDestination = false;

    // 這一場的報名站位（CeStandSpot）；null＝還沒挑過。只在框架執行緒讀寫。
    private CeStandSpot? standSpot;

    public CriticalEncounter(EventData data, Lifestream lifestream, VNavmesh vnav, AutomatorModule module, CriticalEncountersModule source)
        : base(data, lifestream, vnav, module)
    {
        this.source = source;
        source.CriticalEncounters.TryGetValue(data.Id, out lastSeen);

        handlers.Add(ActivityState.WaitingToStartCriticalEncounter, GetWaitingToStartCriticalEncounterChain);
    }

    protected override TaskManagerTask GetPathfindingWatcher(StateManagerModule states)
    {
        var stops = new TravelStopGuard();
        return new TaskManagerTask(() =>
        {
            TakeReturnHandOver(stops);
            if (!IsValid())
            {
                throw new Exception("Activity is no longer valid.");
            }

            // 使用者在手動移動（或導航剛停還沒靜止）時先不挑站位點：挑了就會送出移動。
            if (!finalDestination && IsCloseToZone() && !stops.HoldsOwnMoves(IsNavigating()))
            {
                // Get all players in the zone
                var playersInZone = Svc.Objects
                    .Where(o => o.ObjectKind == ObjectKind.Player)
                    .Where(o => Vector3.Distance(o.Position, GetPosition()) <= GetRadius())
                    .ToList();

                if (TryStandSpot())
                {
                    finalDestination = true;
                }
                else if (playersInZone.Count > 4)
                {
                    var minX = playersInZone.Min(p => p.Position.X);
                    var maxX = playersInZone.Max(p => p.Position.X);
                    var minY = playersInZone.Min(p => p.Position.Z);
                    var maxY = playersInZone.Max(p => p.Position.Z);

                    // Choose a random point within the bounding box of players
                    var rand = new Random();
                    var randX = (float)(minX + rand.NextDouble() * (maxX - minX));
                    var randY = (float)(minY + rand.NextDouble() * (maxY - minY));
                    var randomPoint = new Vector3(randX, GetPosition().Y, randY);

                    module.Debug($"Pathfinding to random point: {randomPoint} (MinX: {minX}, MaxX: {maxX}, MinY: {minY}, MaxY: {maxY})");

                    TravelIntent.Register(randomPoint, TravelSource.CriticalEncounterSpot);
                    vnav.PathfindAndMoveTo(randomPoint, false);
                    finalDestination = true;
                }
            }

            if (!finalDestination && IsInZone())
            {
                if (vnav.IsRunning())
                {
                    vnav.Stop();
                }

                return true;
            }

            if (Encounter.State != DynamicEventState.Register)
            {
                ReportTravelInterrupt("已開打");
                throw new Exception("This event started without you");
            }

            var stopped = !IsNavigating();
            ObserveTravel(stops, !stopped);
            if (finalDestination)
            {
                return stopped && SettleAtStandSpot(states, stops);
            }

            if (PauseForCombat(states, stopped))
            {
                return false;
            }

            if (KeepTravelling(stops, !stopped, TravelSource.IllegalModeCriticalEncounter))
            {
                return false;
            }

            ReportTravelInterrupt("vnav停下");
            throw new VnavmeshStoppedException();
        }, TravelWatcherConfig(stops));
    }

    // 每一場只挑一次：挑到就已送出移動（回 true）；設定關著、兩歧塔、沒挑到回 false，照原本的方式（人多時往人群裡、否則進範圍就停）。
    private bool TryStandSpot()
    {
        if (standSpot != null || !source.Config.RandomRegistrationSpot || !TryGetEncounter(out var encounter) || CeStandSpot.IsTower(encounter))
        {
            return false;
        }

        standSpot = new CeStandSpot("非法模式", data.Id, encounter.Name);
        return standSpot.Begin(vnav, encounter.Position, DestinationRadius) == CeSpotOutcome.Picked;
    }

    // 導航停下時：沒有自己挑的點照舊結束。停在報名範圍內：有人疊著就微調一次（戰鬥中不調），不等它走完、照原本進入等開打，
    // 免得在監看裡多停留時剛好開打而擲「已開打」。停在範圍外＝那一點走不到：改走原本的目的地（之後照原本的方式），
    // 手動移動中、戰鬥中先等。回 false＝監看繼續等。
    private bool SettleAtStandSpot(StateManagerModule states, TravelStopGuard stops)
    {
        if (standSpot?.Spot == null)
        {
            return true;
        }

        if (IsInZone())
        {
            if (!Svc.Condition[ConditionFlag.InCombat])
            {
                standSpot.Arrive(vnav, GetPosition(), DestinationRadius);
            }

            return true;
        }

        if (stops.HoldsOwnMoves(false) || PauseForCombat(states, true))
        {
            return false;
        }

        var left = Player.DistanceTo(GetPosition());
        Svc.Log.Information($"[BOCCHI][CE站位] 來源=非法模式 id={data.Id} name=「{GetName()}」 停在報名範圍外（離中心 {left:F1} 米，報名半徑 {DestinationRadius:F0}）："
                            + $"挑的點多半走不到，改走原本的目的地；角色位置 {Player.Position:F1}");
        standSpot.GiveUp();
        finalDestination = false;
        var destination = DestinationFloor.Project(vnav, GetPosition());
        TravelIntent.Register(destination, TravelSource.IllegalModeCriticalEncounter);
        vnav.PathfindAndMoveTo(destination, false);
        return false;
    }


    private Func<Chain> GetWaitingToStartCriticalEncounterChain(StateManagerModule states)
    {
        return () =>
        {
            return Chain.Create("Illegal:WaitingToStartCriticalEncounter")
                .Then(new TaskManagerTask(() =>
                    {
                        if (!IsValid())
                        {
                            throw new Exception("The critical encounter appears to have started without you.");
                        }

                        if (Encounter.State == DynamicEventState.Battle &&
                            states.GetState() != State.InCriticalEncounter)
                        {
                            throw new Exception("The critical encounter appears to have started without you.");
                        }

                        if (!vnav.IsRunning() && states.GetState() == State.InCombat)
                        {
                            Actions.TryUnmount();

                            if (module.Config.ShouldToggleAiProvider)
                            {
                                module.SetAi(true, "等待緊急遭遇戰開始時遭到攻擊");
                            }
                        }

                        return states.GetState() == State.InCriticalEncounter;
                    },
                    new TaskManagerConfiguration
                    {
                        TimeLimitMS = 180000,
                    }))
                .Then(_ => state = ActivityState.Participating);
        };
    }

    public override unsafe bool IsValid()
    {
        if (!TryGetEncounter(out var encounter))
        {
            return false;
        }

        if (encounter.State == DynamicEventState.Register)
        {
            return true;
        }

        var dec = DynamicEventContainer.GetInstance();
        return dec != null && encounter.DynamicEventId == dec->CurrentEventId;
    }

    protected override float GetRadius()
    {
        // This is kind of an assumption, but it seems accurate enough for most encounters.
        return Encounter.Radius;
    }

    protected override Vector3 GetPosition()
    {
        return Encounter.Position;
    }

    public override string GetName()
    {
        return Encounter.Name;
    }

    /// <summary>報名截止（Unix 秒；0 以下＝沒讀到）：「換一場」用它分辨同一種緊急遭遇戰的不同場次。</summary>
    public int StartTimestamp
    {
        get => Encounter.StartTimestamp;
    }

    public override int ReadProgress()
    {
        return Encounter.Progress;
    }

    public override string KindLabel
    {
        get => "CE";
    }

    protected override string RegistrationSecondsLeft()
    {
        var start = Encounter.StartTimestamp;
        return start > 0 ? (start - DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(CultureInfo.InvariantCulture) : "?";
    }

    // 只用插件自己看到的報名開始（紀錄的「第一次看到」，不早於真正開放）。刻意不從報名截止時間反推：
    // 報名期長度沒有驗證過是固定值，推錯會提早出發；看不到就讓呼叫端從選定算（只會多等）。
    protected override long RegistrationOpenAgeMs(out string unknownReason)
    {
        if (!source.History.TryGetOpen((ushort)data.Id, out var entry))
        {
            unknownReason = "沒有這一場的紀錄";
            return -1;
        }

        if (entry.FirstSeenState != DynamicEventState.Register)
        {
            unknownReason = "第一次看到時已過了報名";
            return -1;
        }

        unknownReason = string.Empty;
        return Math.Max(0L, (long)(DateTime.UtcNow - entry.FirstSeenUtc).TotalMilliseconds);
    }

    private bool IsCloseToZone(float radius = 50f)
    {
        return Player.DistanceTo(GetPosition()) <= radius;
    }


    protected override unsafe bool IsActivityTarget(IBattleNpc obj)
    {
        try
        {
            var battleChara = (BattleChara*)obj.Address;

            var isRelatedToCurrentEvent = battleChara->EventId.EntryId == Player.BattleChara->EventId.EntryId;

            return obj.SubKind == (byte)BattleNpcSubKind.Enemy && isRelatedToCurrentEvent;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex.Message);
            return false;
        }
    }

    protected override ActivityState GetPostPathfindingState()
    {
        return ActivityState.WaitingToStartCriticalEncounter;
    }
}
