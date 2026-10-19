using System;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace BOCCHI.Modules.Treasure;

/// <summary>
/// 聊天裡最近一次魔尋寶的結果（LogMessage 10965／10966，見 <see cref="TreasureSenseText"/>）；<see cref="Sequence"/> 每收到一次加一，
/// 由 <see cref="TreasureModule"/> 取用。範本第一次用到才讀遊戲表。只在框架執行緒讀寫（聊天事件也在框架執行緒）。
/// </summary>
public static class TreasureSenseNotice
{
    private const string Tag = "[BOCCHI][寶箱巡迴]";

    private static TreasureSenseText? template;

    private static bool templateRead;

    private static bool unreadableLogged;

    public static int Sequence { get; private set; }

    public static int Silver { get; private set; }

    public static int Bronze { get; private set; }

    public static void Observe(string text)
    {
        var parser = Template();
        if (parser == null)
        {
            return;
        }

        if (parser.TryParse(text, out var silver, out var bronze))
        {
            Silver = silver;
            Bronze = bronze;
            Sequence++;
            return;
        }

        if (!unreadableLogged && parser.LooksLikeSense(text))
        {
            unreadableLogged = true;
            Svc.Log.Information($"{Tag} 這句像魔尋寶的結果但讀不到數字，當作不知道（之後同樣的情形不再記）：{text}");
        }
    }

    private static TreasureSenseText? Template()
    {
        if (templateRead)
        {
            return template;
        }

        templateRead = true;
        try
        {
            var sheet = Svc.Data.GetExcelSheet<LogMessage>();
            if (sheet.GetRowOrDefault(TreasureSenseText.SenseRow) is not { } sense)
            {
                Svc.Log.Information($"{Tag} 讀不到 LogMessage#{TreasureSenseText.SenseRow}（魔尋寶的結果）：不認得感知數，巡迴照常巡全部生成點");
                return null;
            }

            template = TreasureSenseText.FromSeStrings(sense.Text, sheet.GetRowOrDefault(TreasureSenseText.NoneRow)?.Text);
            Svc.Log.Information(template == null
                ? $"{Tag} LogMessage#{TreasureSenseText.SenseRow} 的形狀不是「文字、數字、文字、數字、文字」：不認得感知數，巡迴照常巡全部生成點"
                : $"{Tag} 魔尋寶結果的範本：{template.Describe()}");
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"{Tag} 讀魔尋寶結果的範本時出錯（{ex.GetType().Name}：{ex.Message}）：不認得感知數，巡迴照常巡全部生成點");
        }

        return template;
    }
}
