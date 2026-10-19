using System.Globalization;
using System.Text.RegularExpressions;

namespace BOCCHI.Modules.Exp;

/// <summary>輔助職業經驗值的聊天訊息模板；第 1 個擷取群組一律是經驗值數字。</summary>
/// <remarks>刻意不依賴 Dalamud，離線驗收工具直接編譯這個檔。</remarks>
public static class ExpMessagePatterns
{
    /// <summary>
    /// 台服 LogMessage#10953：「…的‹輔助職業›獲得了‹kilo(數字,逗號)›‹(+N%)›點輔助職業經驗值。」
    /// 數字帶千分位逗號；加成括號照表是半形，全形一併接受。
    /// </summary>
    public const string TraditionalChinese = @"獲得了([\d,]+)(?:[(（]\+\d+%[)）])?點輔助職業經驗值。";

    /// <summary>台服用來判斷「這句應該是經驗值訊息」的字樣（模板沒命中時才拿來取樣）。</summary>
    public const string TraditionalChineseProbe = "點輔助職業經驗值";

    /// <summary>表字串裡一定要有的文字段；對不上代表改版後模板可能過期。</summary>
    public static readonly string[] TraditionalChineseLiterals = ["獲得了", "點輔助職業經驗值。"];

    public static bool TryParseAmount(Match match, out int amount)
    {
        amount = 0;
        if (!match.Success || match.Groups.Count < 2)
        {
            return false;
        }

        return int.TryParse(match.Groups[1].Value.Replace(",", string.Empty), NumberStyles.None, CultureInfo.InvariantCulture, out amount);
    }
}
