namespace BOCCHI.Pathfinding;

/// <summary>一段巡迴（從開始或續跑到停下）為什麼結束；[寶箱巡迴] 行的「結束=」欄位。</summary>
public enum HuntEnd
{
    Finished,
    Empty,
    Paused,
    Stopped,
    AutoStopped,
    Unloaded,
}

public static class HuntSegment
{
    public static string Label(HuntEnd end)
    {
        return end switch
        {
            HuntEnd.Finished => "走完",
            HuntEnd.Empty => "無點",
            HuntEnd.Paused => "暫停",
            HuntEnd.Stopped => "停止",
            HuntEnd.Unloaded => "卸載",
            _ => "自動停止",
        };
    }

    /// <summary>這一段結束後進度怎麼了：保留（可續跑）、失效（換區或登出，續跑停用）、丟棄、完成。</summary>
    public static string ProgressLabel(HuntEnd end, bool keepProgress, bool invalidating)
    {
        if (end is HuntEnd.Finished or HuntEnd.Empty)
        {
            return "完成";
        }

        if (!keepProgress)
        {
            return "丟棄";
        }

        return invalidating ? "失效" : "保留";
    }
}
