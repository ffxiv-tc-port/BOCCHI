using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BOCCHI.Ledger;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using ClientLanguage = Dalamud.Game.ClientLanguage;

namespace BOCCHI.Modules.Exp;

public class ExpTracker
{
    // 一個 session 最多記幾種「疑似經驗值但沒命中模板」的訊息形狀(同形狀只記一次)。
    private const int MaxUnmatchedShapes = 8;

    /// <summary>台服輔助職業經驗值訊息的 LogMessage 列號。</summary>
    private const uint TraditionalChineseLogMessageRow = 10953;

    // 只在「這個語言沒有已知模板」時才啟用的取樣器。
    private static readonly Regex ExpLikeProbe =
        new(@"經驗|経験|Erfahrung|Phantom|xperience|xpérience", RegexOptions.Compiled);

    private float exp = 0f;

    // null ＝ 這個語言「目前沒有已知的模板」，不是「用英文的那個」。
    private readonly string? pattern;

    // 有模板但沒命中時，含這個字樣的訊息才取樣；null＝有模板的語言不取樣（舊行為）。
    private readonly string? unmatchedProbe;

    private DateTime startTime = DateTime.UtcNow;

    private bool loggedFirstMatch;

    private readonly HashSet<string> loggedShapes = new();

    public ExpTracker()
    {
        Reset();
        var language = Svc.ClientState.ClientLanguage;
        pattern = GetExpMessagePattern(language);
        var isTraditionalChinese = language is (ClientLanguage)5 or (ClientLanguage)7;
        unmatchedProbe = isTraditionalChinese ? ExpMessagePatterns.TraditionalChineseProbe : null;

        // 要使用者回報的診斷。
        if (pattern == null)
        {
            Svc.Log.Information(
                $"[BOCCHI][Exp] 目前用戶端語言({language}/{(int)language})沒有已知的幻靈支援經驗值訊息模板，" +
                $"每小時經驗值不會計數。之後若出現疑似訊息，會在此最多記錄 {MaxUnmatchedShapes} 種形狀供補模板用。");
        }
        else
        {
            Svc.Log.Information($"[BOCCHI][Exp] 幻靈支援經驗值訊息模板已套用({language}/{(int)language})：{pattern}");
        }

        if (isTraditionalChinese)
        {
            checkTraditionalChineseTemplate();
        }
    }

    public void OnTerritoryChange(ushort _)
    {
        Reset();
    }

    public void OnChatMessage(XivChatType type, int timestamp, SeString sender, SeString message, bool isHandled)
    {
        var text = message.ToString();

        if (pattern == null)
        {
            logUnmatchedShape(text, message);
            return;
        }

        var match = Regex.Match(text, pattern);
        if (!ExpMessagePatterns.TryParseAmount(match, out var amount))
        {
            if (unmatchedProbe != null && text.Contains(unmatchedProbe, StringComparison.Ordinal))
            {
                logUnmatchedShape(text, message);
            }

            return;
        }

        exp += amount;

        if (!loggedFirstMatch)
        {
            loggedFirstMatch = true;
            Svc.Log.Information($"[BOCCHI][Exp] 幻靈支援經驗值訊息首次命中(本 session 只記這一次)：{ChatPlayerMask.ForLog(message, out _)}");
        }
    }

    public void Reset()
    {
        exp = 0f;
        startTime = DateTime.UtcNow;
    }

    public float GetExpPerHour()
    {
        var elapsed = (float)(DateTime.UtcNow - startTime).TotalHours;
        if (elapsed <= 0)
        {
            return 0;
        }

        return exp / elapsed;
    }

    private void logUnmatchedShape(string text, SeString message)
    {
        if (loggedShapes.Count >= MaxUnmatchedShapes || !ExpLikeProbe.IsMatch(text))
        {
            return;
        }

        // 把數字抽掉當「形狀」，同一句話只會記一次，不會被連續打怪洗版；玩家名先遮掉（記錄不寫名字，也不會因為人不同多佔形狀）。
        var masked = ChatPlayerMask.ForLog(message, out _);
        var shape = Regex.Replace(masked, @"\d+", "#");
        if (!loggedShapes.Add(shape))
        {
            return;
        }

        Svc.Log.Information(
            $"[BOCCHI][Exp] 未命中模板的疑似經驗值訊息(形狀 {loggedShapes.Count}/{MaxUnmatchedShapes}，同形狀只記一次)：{masked}");
    }

    // 模板是照台服表字串寫死的；改版後表字串變了，這裡在 log 留一行，不影響計數。
    private static void checkTraditionalChineseTemplate()
    {
        try
        {
            var row = Svc.Data.GetExcelSheet<LogMessage>().GetRowOrDefault(TraditionalChineseLogMessageRow);
            var text = row?.Text.ExtractText() ?? string.Empty;
            var missing = ExpMessagePatterns.TraditionalChineseLiterals.Where(s => !text.Contains(s, StringComparison.Ordinal)).ToArray();
            Svc.Log.Information(missing.Length == 0
                ? $"[BOCCHI][Exp] LogMessage#{TraditionalChineseLogMessageRow} 與內建繁中模板一致。"
                : $"[BOCCHI][Exp] LogMessage#{TraditionalChineseLogMessageRow} 缺少模板文字段（{string.Join("、", missing)}），模板可能已過期：{text}");
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[BOCCHI][Exp] 讀不到 LogMessage#{TraditionalChineseLogMessageRow}，略過模板核對：{ex.Message}");
        }
    }

    /// <summary>這個語言的輔助職業經驗值訊息模板；null＝沒有已知的模板。場次帳也用它計數。</summary>
    internal static string? GetExpMessagePattern(ClientLanguage clientLanguage)
    {
        return clientLanguage switch
        {
            ClientLanguage.French => @"Vous gagnez (\d+) points d'expérience de soutien en .+? fantôme",
            ClientLanguage.German => @"Du erhältst (\d+) Phantomroutine als Phantom",
            ClientLanguage.Japanese => @".+?」に(\d+)ポイントのサポート経験値を得た。",

            // 台服(繁中)：ClientLanguage 實測回報 7；5 是同一支 fork 裡的 ChineseTraditional。
            // 用數值轉型而不是列舉成員名(上游 Dalamud 的列舉沒有 TraditionalChinese 這個名字)。
            (ClientLanguage)5 or (ClientLanguage)7 => ExpMessagePatterns.TraditionalChinese,

            _ => @"You gain (\d+) Phantom .+? experience points\.",
        };
    }
}
