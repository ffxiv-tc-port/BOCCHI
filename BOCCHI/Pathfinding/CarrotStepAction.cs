namespace BOCCHI.Pathfinding;

/// <summary>巡迴插隊挖幸運胡蘿蔔那一步（<see cref="PathfinderStepType.DigCarrot"/>）這一拍怎麼做。</summary>
public enum CarrotStepAction
{
    /// <summary>走向胡蘿蔔（照巡迴的移動規則：讓位、重送上限、叫坐騎）。</summary>
    Walk,

    /// <summary>不移動：挖的動作鏈剛送出，或挖完在等胡蘿蔔物件消失。</summary>
    Hold,

    /// <summary>這一步結束（挖到或放棄）：移除它，接回原本那一步。</summary>
    Done,
}
