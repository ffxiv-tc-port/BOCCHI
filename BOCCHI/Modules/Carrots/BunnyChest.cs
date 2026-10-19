using System;
using System.Numerics;
using BOCCHI.Automation;
using BOCCHI.Enums;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Ocelot.IPC;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace BOCCHI.Modules.Carrots;

public enum BunnyChestAction : byte
{
    /// <summary>這一拍什麼都不送（開箱事件進行中，或還在互動節流內）。</summary>
    Wait,

    /// <summary>不在互動距離內：走近。</summary>
    MoveCloser,

    Interact,

    /// <summary>寶箱不見了，或開箱事件跑完一段時間：結束。</summary>
    Done,
}

/// <summary>開黃金財寶箱（幸福兔挖出來的寶箱）的每拍決定；只看傳進來的數字，純邏輯（離線驗收直接呼叫）。</summary>
/// <remarks>開箱不詠唱，是一段 OccupiedInQuestEvent／OccupiedInEvent：互動後等事件或寶箱消失，不看詠唱、不看互動回傳值。</remarks>
public static class BunnyChestRules
{
    /// <summary>同一個寶箱送過互動之後，這麼久內不再送（事件通常一兩幀內就開始）。</summary>
    public const long InteractIntervalMs = 2_000;

    /// <summary>開箱事件結束後寶箱還在，再等這麼久就當作開過了、不再互動。</summary>
    public const long EventSettleMs = 3_000;

    /// <param name="occupied">這一拍在開箱一類的事件裡。</param>
    /// <param name="eventSeen">送過互動之後看過事件。</param>
    /// <param name="sinceEventEndedMs">事件結束到現在；事件還沒結束或沒看過時給 0。</param>
    public static BunnyChestAction Next(bool occupied, bool chestPresent, bool inRange, bool eventSeen, long sinceEventEndedMs, long now, long nextInteractAt)
    {
        if (occupied)
        {
            return BunnyChestAction.Wait;
        }

        if (!chestPresent)
        {
            return BunnyChestAction.Done;
        }

        if (eventSeen)
        {
            return sinceEventEndedMs >= EventSettleMs ? BunnyChestAction.Done : BunnyChestAction.Wait;
        }

        if (!inRange)
        {
            return BunnyChestAction.MoveCloser;
        }

        return now >= nextInteractAt ? BunnyChestAction.Interact : BunnyChestAction.Wait;
    }
}

/// <summary>開箱的記帳（看過事件沒、事件何時結束、互動節流）與每拍決定；不碰遊戲（離線驗收直接驅動）。</summary>
public sealed class BunnyChestTracker
{
    private long nextInteractAt;

    private long eventEndedAt;

    /// <summary>送過互動之後看過開箱事件。</summary>
    public bool EventSeen { get; private set; }

    /// <summary>決定要互動的次數（每次之後 <see cref="BunnyChestRules.InteractIntervalMs"/> 內不再互動）。</summary>
    public int Interacts { get; private set; }

    /// <param name="occupied">這一拍在開箱一類的事件裡。</param>
    public BunnyChestAction Tick(bool occupied, bool present, bool inRange, long now)
    {
        if (occupied)
        {
            EventSeen |= Interacts > 0;
            eventEndedAt = 0;
        }
        else if (EventSeen && eventEndedAt == 0)
        {
            eventEndedAt = now;
        }

        var since = EventSeen && eventEndedAt != 0 ? now - eventEndedAt : 0;
        var action = BunnyChestRules.Next(occupied, present, inRange, EventSeen, since, now, nextInteractAt);
        if (action == BunnyChestAction.Interact)
        {
            nextInteractAt = now + BunnyChestRules.InteractIntervalMs;
            Interacts++;
        }

        return action;
    }
}

/// <summary>
/// 開一個黃金財寶箱（幸運胡蘿蔔巡迴與寶箱巡迴順路挖胡蘿蔔共用）：存 GameObjectId、每拍重查物件表，
/// 照 <see cref="BunnyChestRules"/> 決定走近、互動或結束。框架執行緒專用，每開一個建一個；不保存物件包裝或原生指標。
/// </summary>
public sealed class BunnyChestOpener
{
    // 讀不到遊戲的互動距離檢查時，用這個直線距離代替。
    private const float FallbackDistance = 3f;

    private const long MoveIntervalMs = 2_000;

    private readonly BunnyChestTracker tracker = new();

    private long nextMoveAt;

    public ulong ChestId { get; private set; }

    /// <summary>找到過寶箱。</summary>
    public bool Seen { get; private set; }

    /// <summary>送過互動之後看過開箱事件。</summary>
    public bool EventSeen
    {
        get => tracker.EventSeen;
    }

    /// <summary>結束時寶箱已經不在（或不能選取）。</summary>
    public bool Gone { get; private set; }

    public int Interacts
    {
        get => tracker.Interacts;
    }

    /// <summary>有開成（看過事件，或互動後寶箱消失）。</summary>
    public bool Opened
    {
        get => EventSeen || (Gone && Interacts > 0);
    }

    /// <summary>找離角色最近、可選取的黃金財寶箱（<paramref name="radius"/> 米內），記下 id；找到回 true。</summary>
    public bool TryFind(float radius)
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        var from = player.Position;
        var best = float.MaxValue;
        foreach (var obj in Svc.Objects)
        {
            if (obj.ObjectKind != ObjectKind.EventObj || obj.BaseId != (uint)OccultObjectType.BunnyChest || !obj.IsTargetable)
            {
                continue;
            }

            var distance = Vector3.Distance(from, obj.Position);
            if (distance <= radius && distance < best)
            {
                best = distance;
                ChestId = obj.GameObjectId;
                Seen = true;
            }
        }

        return Seen;
    }

    /// <summary>每拍呼叫；回 true＝結束（看 <see cref="Opened"/>／<see cref="Gone"/>）。<paramref name="vnav"/> 為 null 時不走近。</summary>
    public unsafe bool Step(VNavmesh? vnav)
    {
        var targets = TargetSystem.Instance();
        if (targets == null)
        {
            return false;
        }

        var now = Environment.TickCount64;
        var condition = Svc.Condition;
        var occupied = condition[ConditionFlag.OccupiedInEvent] || condition[ConditionFlag.OccupiedInQuestEvent] || condition[ConditionFlag.Occupied];
        var chest = Svc.Objects.SearchById(ChestId);
        var present = chest is { ObjectKind: ObjectKind.EventObj, IsTargetable: true } && chest.BaseId == (uint)OccultObjectType.BunnyChest;
        var player = Svc.Objects.LocalPlayer;
        var inRange = present && player != null && InInteractRange(player, chest!);
        switch (tracker.Tick(occupied, present, inRange, now))
        {
            case BunnyChestAction.Done:
                Gone = !present;
                return true;
            case BunnyChestAction.MoveCloser:
                if (vnav != null && now >= nextMoveAt && !vnav.IsRunning() && !vnav.IsSimpleMoveInProgress())
                {
                    nextMoveAt = now + MoveIntervalMs;
                    var at = chest!.Position;
                    OwnMoveDestinations.Note(at);
                    vnav.PathfindAndMoveTo(at, false);
                }

                return false;
            case BunnyChestAction.Interact:
                if (vnav?.IsRunning() == true)
                {
                    vnav.Stop();
                }

                Svc.Targets.Target = chest;
                OwnInteraction.Note(now);
                targets->InteractWithObject((GameObject*)chest!.Address);
                return false;
            default:
                return false;
        }
    }

    // 遊戲自己互動前的距離檢查（真 bool）；位址解不出來時退回直線距離。
    private static unsafe bool InInteractRange(IGameObject player, IGameObject target)
    {
        if (EventFramework.MemberFunctionPointers.CheckInteractRange is not null)
        {
            var framework = EventFramework.Instance();
            if (framework != null)
            {
                try
                {
                    return framework->CheckInteractRange((GameObject*)player.Address, (GameObject*)target.Address, 1, false);
                }
                catch (InvalidOperationException)
                {
                    // 特徵碼沒解出來：照直線距離。
                }
            }
        }

        return Vector3.Distance(player.Position, target.Position) <= FallbackDistance;
    }
}
