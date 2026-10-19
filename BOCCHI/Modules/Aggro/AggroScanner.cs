using System;
using BOCCHI.Data;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace BOCCHI.Modules.Aggro;

/// <summary>
/// 每次 Update（框架執行緒）走訪一次物件表，把範圍內未戰鬥的敵對怪寫進可重用快照。
/// 不保留 IGameObject 或原生指標：位址只在同一次走訪裡讀欄位。
/// </summary>
public sealed class AggroScanner
{
    public const float ScanRange = 60f;

    private const int MaxMismatchLogs = 10;

    public readonly AggroSnapshot Snapshot = new();

    public readonly LevelCalibration Calibration = new();

    private int mismatchLogs;

    private bool loggedCalibrationPass;

    public unsafe void Scan(in AggroContext context)
    {
        Snapshot.Begin();

        var player = Svc.Objects.LocalPlayer;
        if (player == null || ZoneData.IsInForkedTowerNoAlloc(player))
        {
            Snapshot.Complete(Environment.TickCount64);
            return;
        }

        var origin = player.Position;
        const float rangeSquared = ScanRange * ScanRange;
        var judgeContext = context with { LevelSourceReliable = Calibration.Reliable };

        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc npc || npc.BattleNpcKind != BattleNpcSubKind.Enemy)
            {
                continue;
            }

            var position = npc.Position;
            var distanceSquared = (position - origin).LengthSquared();
            if (distanceSquared > rangeSquared || (npc.StatusFlags & StatusFlags.InCombat) != 0)
            {
                continue;
            }

            if (npc.IsDead || !npc.IsTargetable)
            {
                continue;
            }

            var level = (int)((BattleChara*)npc.Address)->ForayInfo.Level;
            var nameId = npc.NameId;
            var objectId = npc.GameObjectId;
            var found = OccultAggroData.ByNameId.TryGetValue(nameId, out var info);

            if (found)
            {
                Observe(objectId, nameId, in info, level, ref judgeContext);
            }

            var state = AggroJudge.Judge(found, in info, level, in judgeContext, out var senses, out var bloodActive);
            Snapshot.Offer(new AggroEntry
            {
                GameObjectId = objectId,
                NameId = nameId,
                Position = position,
                Rotation = npc.Rotation,
                HitboxRadius = npc.HitboxRadius,
                Level = level,
                Senses = senses,
                State = state,
                BloodActive = bloodActive,
                DistanceSquared = distanceSquared,
            });
        }

        Snapshot.Complete(Environment.TickCount64);
    }

    private void Observe(ulong objectId, uint nameId, in AggroInfo info, int level, ref AggroContext judgeContext)
    {
        switch (Calibration.Observe(objectId, in info, level))
        {
            case LevelCalibration.Outcome.Match:
                if (!loggedCalibrationPass && Calibration.Matches >= 5 && Calibration.Mismatches == 0)
                {
                    loggedCalibrationPass = true;
                    Svc.Log.Information($"[BOCCHI][仇恨顯示] 等級來源校準：已比對 {Calibration.Matches} 隻固定等級的怪，全部與表相符");
                }

                break;
            case LevelCalibration.Outcome.Mismatch:
            case LevelCalibration.Outcome.BecameUnreliable:
                if (mismatchLogs < MaxMismatchLogs)
                {
                    mismatchLogs++;
                    Svc.Log.Information($"[BOCCHI][仇恨顯示] 等級與表不符：NameId {nameId} 表上 Lv{info.FixedLevel}、物件上讀到 Lv{level}（相符 {Calibration.Matches}／不符 {Calibration.Mismatches}）");
                }

                if (!Calibration.Reliable && judgeContext.LevelSourceReliable)
                {
                    judgeContext = judgeContext with { LevelSourceReliable = false };
                    Svc.Log.Information($"[BOCCHI][仇恨顯示] 物件上的等級不可靠（相符 {Calibration.Matches}／不符 {Calibration.Mismatches}）：之後固定等級的怪改用表上等級、多等級的怪一律當「可能」");
                }

                break;
        }
    }
}
