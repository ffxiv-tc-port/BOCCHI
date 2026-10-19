using System;
using BOCCHI.Data;
using Ocelot.Config.Attributes;
using Ocelot.Modules;

namespace BOCCHI.Modules.Pathfinder;

[Text("config.text")]
public class PathfinderConfig : ModuleConfig
{
    /// <summary>區域等級上限的最大值（MaxLevel 的值域 1..此值）；資料裡等級比這高的點永遠不去。</summary>
    public const int LevelCeiling = 28;

    [FloatRange(50f, 500f)] public float ReturnCost { get; set; } = 300f;

    [FloatRange(10f, 500f)] public float TeleportCost { get; set; } = 50f;

    [FloatRange(10f, 100f)]
    [RangeIndicator(0.9f, 0.1f, 0.6f)]
    public float DetectionRange { get; set; } = 75f;

    [IntRange(1, LevelCeiling)] [Experimental] public int MaxLevel { get; set; } = 23;

    [Checkbox] [Experimental] public bool FollowKnowledgeLevel { get; set; } = false;

    /// <summary>巡迴暫停後多久內可以續跑（分鐘）；0＝不限。超過就只能從頭開始（跑過的點可能已重新出現寶箱或胡蘿蔔）。</summary>
    [IntRange(0, 180)] public int ResumeExpiryMinutes { get; set; } = 30;

    /// <summary>巡迴走向點時導航停了、角色卻持續自己移動（使用者在操作）就先不重送，靜止後再接續；false＝照舊每次停下都重送。</summary>
    [Checkbox] public bool YieldToManualMovement { get; set; } = true;

    /// <summary>
    /// 實際採用的區域等級上限，值域同 MaxLevel（1..28）。跟隨知見等級時＝真實知見等級－1（只去怪物不會主動攻擊的區域），
    /// 知見已滿級或讀不到知見等級就沿用 MaxLevel。
    /// </summary>
    public static int EffectiveMaxLevel(bool followKnowledgeLevel, int maxLevel, KnowledgeLevelReading knowledge)
    {
        return ChooseMaxLevel(followKnowledgeLevel, maxLevel, knowledge).Level;
    }

    /// <summary>同 <see cref="EffectiveMaxLevel"/>，另外回報走的是哪一條。純函式。</summary>
    public static MaxLevelChoice ChooseMaxLevel(bool followKnowledgeLevel, int maxLevel, KnowledgeLevelReading knowledge)
    {
        if (!followKnowledgeLevel)
        {
            return new MaxLevelChoice(maxLevel, MaxLevelSource.Manual);
        }

        if (!knowledge.IsAvailable)
        {
            return new MaxLevelChoice(maxLevel, MaxLevelSource.KnowledgeUnavailable);
        }

        // 滿級時「知見－1」會永遠少一級（上限 20 時卡在 19），使用者手動設的上限才是他要的範圍。
        if (knowledge.Real >= KnowledgeLevel.SouthHornCap)
        {
            return new MaxLevelChoice(maxLevel, MaxLevelSource.KnowledgeAtCap);
        }

        return new MaxLevelChoice(Math.Clamp(knowledge.Real - 1, 1, LevelCeiling), MaxLevelSource.FollowKnowledge);
    }
}

/// <summary>區域等級上限從哪裡來：設定值／跟隨知見等級／跟隨但知見已滿級（用設定值）／跟隨但讀不到（用設定值）。</summary>
public enum MaxLevelSource
{
    Manual,
    FollowKnowledge,
    KnowledgeAtCap,
    KnowledgeUnavailable,
}

public readonly record struct MaxLevelChoice(int Level, MaxLevelSource Source);
