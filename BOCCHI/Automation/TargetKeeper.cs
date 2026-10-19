using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameFunctions;
using ECommons.GameHelpers;

namespace BOCCHI.Automation;

// 本檔契約：只在框架執行緒用。只存 GameObjectId 與時間戳；物件包裝只在呼叫當下用，不跨幀保存。

/// <summary>非法模式與自動打怪改寫目標的入口：照 <see cref="TargetHysteresis"/> 決定寫不寫，記下 BOCCHI 自己寫了哪一隻。</summary>
public sealed class TargetKeeper
{
    private TargetCandidate[] buffer = new TargetCandidate[16];

    private TargetHoldState state;

    public readonly TargetStats Stats = new();

    /// <summary>記錄裡的「來源=」：FATE#id、CE#id、自動打怪。</summary>
    public string Source { get; set; } = "?";

    public void Reset()
    {
        state = default;
        Stats.Reset();
    }

    /// <summary>只看不寫：記下目前目標（「被別人改走」的計數用）。</summary>
    public void Observe(ulong currentId)
    {
        Stats.Observe(currentId, state.LastWrittenId);
    }

    /// <param name="enemies">當幀的候選，離玩家由近到遠。</param>
    /// <param name="holdSeconds">「目標最短維持秒數」；0＝舊行為。</param>
    /// <param name="reason">寫進記錄的情境；傳常數，不要內插。</param>
    public TargetDecision Apply(IReadOnlyList<IBattleNpc> enemies, TargetPickMode mode, int holdSeconds, string reason)
    {
        var count = Fill(enemies);
        var current = ReadCurrent();
        Stats.Observe(current.Id, state.LastWrittenId);
        var now = Environment.TickCount64;
        var decision = TargetHysteresis.Decide(buffer.AsSpan(0, count), mode, current, ref state, holdSeconds * 1000L, now);
        Stats.NoteDecision(decision, current.Id);
        if (decision.Writes && decision.TargetId != current.Id)
        {
            var next = Find(enemies, decision.TargetId);
            if (next != null || decision.TargetId == 0)
            {
                Svc.Targets.Target = next;
                Stats.NoteWrite(decision.TargetId, now);
                Svc.Log.Debug(TargetStats.ChangeLine(current.Id, decision.TargetId, reason, PickLabel(decision, mode), Source));
            }
        }

        return decision;
    }

    /// <summary>目前目標的當幀快照；沒有目標回 Id 0。</summary>
    public static CurrentTarget ReadCurrent()
    {
        var target = Svc.Targets.Target;
        if (target == null)
        {
            return default;
        }

        var playerId = Svc.Objects.LocalPlayer?.GameObjectId ?? 0;
        var legal = target is IBattleNpc { IsDead: false, IsTargetable: true } npc && npc.IsHostile();
        return new CurrentTarget(target.GameObjectId, legal, playerId != 0 && target.TargetObjectId == playerId);
    }

    private static string PickLabel(in TargetDecision decision, TargetPickMode mode)
    {
        var pick = mode == TargetPickMode.Centroid ? "重心" : "最近";
        return decision.Verdict == TargetVerdict.Legacy ? pick + "（舊行為）" : pick;
    }

    private int Fill(IReadOnlyList<IBattleNpc> enemies)
    {
        if (buffer.Length < enemies.Count)
        {
            buffer = new TargetCandidate[Math.Max(enemies.Count, buffer.Length * 2)];
        }

        for (var i = 0; i < enemies.Count; i++)
        {
            var enemy = enemies[i];
            buffer[i] = new TargetCandidate(enemy.GameObjectId, enemy.Position, Player.DistanceTo(enemy));
        }

        return enemies.Count;
    }

    private static IBattleNpc? Find(IReadOnlyList<IBattleNpc> enemies, ulong id)
    {
        if (id == 0)
        {
            return null;
        }

        for (var i = 0; i < enemies.Count; i++)
        {
            if (enemies[i].GameObjectId == id)
            {
                return enemies[i];
            }
        }

        return null;
    }
}
