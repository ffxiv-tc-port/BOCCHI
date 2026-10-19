using System;
using System.Numerics;
using BOCCHI.Modules.Aggro;

namespace BOCCHI.Modules.Automator;

// 本檔契約：純邏輯，不碰遊戲、不配置（離線測試直接呼叫）。時間一律 Environment.TickCount64 的毫秒。

public enum EarlyEngageVerdict : byte
{
    Engage = 0,

    /// <summary>設定關著，或這一場已經退回舊行為。</summary>
    Off = 1,

    TooFar = 2,

    /// <summary>目標還沒跟任何人打。</summary>
    NotEngaged = 3,

    /// <summary>停下的位置在別的魔物的感知範圍內：照舊走近。</summary>
    InAggroZone = 4,
}

/// <summary>F.A.T.E. 魔物已經在戰鬥中時提早下坐騎接戰（非法模式趕路監看用）。</summary>
public static class EarlyEngageRules
{
    /// <summary>提早接戰之後這麼久都沒進入戰鬥，就退回舊行為（走近到接戰距離）。</summary>
    public const long CombatWaitMs = 10_000;

    /// <summary>ClassJob.Role：1 防護、2 近戰、3 遠程（物理與魔法）、4 治療；0（讀不到）當近戰，照舊用接戰距離。</summary>
    public static bool IsMeleeRole(byte role)
    {
        return role is 0 or 1 or 2;
    }

    public static string RoleLabel(byte role)
    {
        return role switch
        {
            1 => "防護職業",
            2 => "近戰職業",
            3 => "遠程職業",
            4 => "治療職業",
            _ => "職業類型不明",
        };
    }

    public static float RangeFor(byte role, float meleeRange, float rangedRange)
    {
        return IsMeleeRole(role) ? meleeRange : rangedRange;
    }

    /// <param name="distance">到目標碰撞圈的距離（米）。前提：在 F.A.T.E. 裡、目標是這場的魔物、還沒到接戰距離。</param>
    public static EarlyEngageVerdict Evaluate(bool enabled, float distance, float range, bool engaged, bool inAggroZone)
    {
        if (!enabled)
        {
            return EarlyEngageVerdict.Off;
        }

        if (distance > range)
        {
            return EarlyEngageVerdict.TooFar;
        }

        if (!engaged)
        {
            return EarlyEngageVerdict.NotEngaged;
        }

        return inAggroZone ? EarlyEngageVerdict.InAggroZone : EarlyEngageVerdict.Engage;
    }

    /// <param name="engagedAt">提早接戰的時間；0＝這一場沒有提早接戰。</param>
    public static bool ShouldFallBack(long engagedAt, bool combatSeen, long now)
    {
        return engagedAt != 0 && !combatSeen && now - engagedAt >= CombatWaitMs;
    }

    /// <summary>位置在任一個狀態不低於 minState、而且不是 exceptId 那隻魔物的區域內。</summary>
    public static bool InAnyZone(ReadOnlySpan<AggroZone> zones, AggroState minState, ulong exceptId, Vector3 position)
    {
        if (minState == AggroState.None)
        {
            minState = AggroState.Possible;
        }

        foreach (ref readonly var zone in zones)
        {
            if (zone.State >= minState && zone.GameObjectId != exceptId && AggroZones.Contains(zone, position))
            {
                return true;
            }
        }

        return false;
    }
}
