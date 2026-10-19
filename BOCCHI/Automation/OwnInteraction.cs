namespace BOCCHI.Automation;

/// <summary>
/// BOCCHI 自己最近一次對物件送出互動（開寶箱、開黃金財寶箱）的時刻：讓位判斷靠它分辨互動事件是不是使用者自己開的。
/// 只在 framework 執行緒讀寫。時間用 Environment.TickCount64，另用旗標記「送過沒」。
/// </summary>
public static class OwnInteraction
{
    private static bool noted;

    private static long lastAt;

    public static void Note(long now)
    {
        noted = true;
        lastAt = now;
    }

    public static bool Recent(long now, long windowMs)
    {
        return noted && now - lastAt <= windowMs;
    }
}
