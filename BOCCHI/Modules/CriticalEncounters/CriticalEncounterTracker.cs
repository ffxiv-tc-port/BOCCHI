using System;
using System.Collections.Generic;
using BOCCHI.Data;
using BOCCHI.Modules.Fates;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using ExcelDynamicEvent = Lumina.Excel.Sheets.DynamicEvent;

namespace BOCCHI.Modules.CriticalEncounters;

public class CriticalEncounterTracker
{
    /// <summary>當幀的受管理快照；拿不到新月島指揮器（離島、讀取中）時是空的。只在 framework 執行緒改動。</summary>
    public Dictionary<uint, CriticalEncounterSnapshot> CriticalEncounters = new();

    public Dictionary<uint, EventProgress> Progress { get; } = new();

    /// <summary>當幀遊戲的「目前參加中的緊急遭遇戰」；0＝沒有，或拿不到指揮器。</summary>
    public ushort CurrentEventId { get; private set; }

    public TowerTimer TowerTimer { get; private set; }

    // Store last known states of each event by ID
    private readonly Dictionary<uint, DynamicEventState> lastStates = new();

    private readonly Dictionary<ushort, string> names = new();

    private bool hadDirector;

    public CriticalEncounterTracker(CriticalEncountersModule module)
    {
        TowerTimer = new TowerTimer(this, module.GetModule<FatesModule>());
    }

    public event Action<CriticalEncounterSnapshot>? OnInactiveState;

    public event Action<CriticalEncounterSnapshot>? OnRegisterState;

    public event Action<CriticalEncounterSnapshot>? OnWarmupState;

    public event Action<CriticalEncounterSnapshot>? OnBattleState;


    public unsafe void Tick(IFramework _)
    {
        // 指揮器類型不是新月島時 GetInstance 回 null；每幀重取，不保存任何原生指標或結構。
        var director = PublicContentOccultCrescent.GetInstance();
        CriticalEncounters.Clear();
        CurrentEventId = director == null ? (ushort)0 : director->DynamicEventContainer.CurrentEventId;
        if (director == null)
        {
            if (hadDirector)
            {
                hadDirector = false;
                Svc.Log.Information("[BOCCHI][CE] 拿不到新月島指揮器（離島或讀取中），緊急遭遇戰清單先清空。");
            }

            return;
        }

        if (!hadDirector)
        {
            hadDirector = true;
            Svc.Log.Information("[BOCCHI][CE] 已取得新月島指揮器，開始追蹤緊急遭遇戰。");
        }

        foreach (ref readonly var native in director->DynamicEventContainer.Events)
        {
            var id = native.DynamicEventId;
            CriticalEncounters[id] = new CriticalEncounterSnapshot(
                id,
                native.EventType,
                native.State,
                native.Progress,
                native.StartTimestamp,
                native.SecondsLeft,
                native.Participants,
                native.Unknown4,
                native.MapMarker.Position,
                GetName(id));
        }

        foreach (var ev in CriticalEncounters.Values)
        {
            // Get previous state, default to Inactive if unknown
            lastStates.TryGetValue(ev.DynamicEventId, out var previousState);

            var currentState = ev.State;

            if (currentState == DynamicEventState.Battle)
            {
                if (ev.Progress == 0)
                {
                    continue;
                }

                if (!Progress.TryGetValue(ev.DynamicEventId, out var current))
                {
                    current = new EventProgress();
                    Progress[ev.DynamicEventId] = current;
                }

                if (current.samples.Count == 0 || current.samples[^1].Progress != ev.Progress)
                {
                    current.Add(ev.Progress);
                }

                if (ev.Progress == 100)
                {
                    Progress.Remove(ev.DynamicEventId);
                }
            }
            else
            {
                Progress.Remove(ev.DynamicEventId);
            }

            if (previousState == currentState)
            {
                continue;
            }

            lastStates[ev.DynamicEventId] = currentState;

            switch (currentState)
            {
                case DynamicEventState.Inactive:
                    OnInactiveState?.Invoke(ev);
                    break;

                case DynamicEventState.Register:
                    OnRegisterState?.Invoke(ev);
                    break;

                case DynamicEventState.Warmup:
                    OnWarmupState?.Invoke(ev);
                    break;

                case DynamicEventState.Battle:
                    OnBattleState?.Invoke(ev);
                    break;
            }
        }
    }

    /// <summary>
    /// 換區時清掉上一個島的狀態：每次進島都與插件載入後第一次進島相同，進島時已在報名的那幾場會各提示一次。
    /// 離島後 <see cref="Tick"/> 不再被呼叫，所以不能只靠「拿不到指揮器」那一支清。只在 framework 執行緒呼叫。
    /// </summary>
    public void Reset()
    {
        var states = lastStates.Count;
        lastStates.Clear();
        Progress.Clear();
        CriticalEncounters.Clear();
        CurrentEventId = 0;
        hadDirector = false;
        if (states > 0)
        {
            Svc.Log.Information($"[BOCCHI][CE] 換區：清掉這次在島上記下的 {states} 場緊急遭遇戰狀態，下次進島重新比對。");
        }
    }

    private string GetName(ushort id)
    {
        if (names.TryGetValue(id, out var name))
        {
            return name;
        }

        name = Svc.Data.GetExcelSheet<ExcelDynamicEvent>().GetRowOrDefault(id)?.Name.ExtractText() ?? string.Empty;
        if (name.Length == 0)
        {
            name = $"#{id}";
        }

        names[id] = name;
        return name;
    }
}
