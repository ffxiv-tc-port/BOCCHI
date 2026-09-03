using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.DalamudServices;
using ClientLanguage = Dalamud.Game.ClientLanguage;

namespace BOCCHI.Modules.Exp;

public class ExpTracker
{
    // 一個 session 最多記幾種「疑似經驗值但沒命中模板」的訊息形狀(同形狀只記一次)。
    private const int MaxUnmatchedShapes = 8;

    // 只在「這個語言沒有已知模板」時才啟用的取樣器。台服真的開放新月島之後，
    // 可以直接從使用者的 log 拿到逐字的繁中模板，不必再猜。
    private static readonly Regex ExpLikeProbe =
        new(@"經驗|経験|Erfahrung|Phantom|xperience|xpérience", RegexOptions.Compiled);

    private float exp = 0f;

    // null ＝ 這個語言「目前沒有已知的模板」，不是「用英文的那個」。
    private readonly string? pattern;

    private DateTime startTime = DateTime.UtcNow;

    private bool loggedFirstMatch;

    private readonly HashSet<string> loggedShapes = new();

    public ExpTracker()
    {
        Reset();
        var language = Svc.ClientState.ClientLanguage;
        pattern = getExpMessagePattern(language);

        // 使用者跑 LogLevel 1，診斷一律寫 Information。
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
            logUnmatchedShape(text);
            return;
        }

        var match = Regex.Match(text, pattern);
        if (!match.Success)
        {
            return;
        }

        exp += int.Parse(match.Groups[1].Value);

        if (!loggedFirstMatch)
        {
            loggedFirstMatch = true;
            Svc.Log.Information($"[BOCCHI][Exp] 幻靈支援經驗值訊息首次命中(本 session 只記這一次)：{text}");
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

    private void logUnmatchedShape(string text)
    {
        if (loggedShapes.Count >= MaxUnmatchedShapes || !ExpLikeProbe.IsMatch(text))
        {
            return;
        }

        // 把數字抽掉當「形狀」，同一句話只會記一次，不會被連續打怪洗版。
        var shape = Regex.Replace(text, @"\d+", "#");
        if (!loggedShapes.Add(shape))
        {
            return;
        }

        Svc.Log.Information(
            $"[BOCCHI][Exp] 未命中模板的疑似經驗值訊息(形狀 {loggedShapes.Count}/{MaxUnmatchedShapes}，同形狀只記一次)：{text}");
    }

    private string? getExpMessagePattern(ClientLanguage clientLanguage)
    {
        return clientLanguage switch
        {
            ClientLanguage.French => @"Vous gagnez (\d+) points d'expérience de soutien en .+? fantôme",
            ClientLanguage.German => @"Du erhältst (\d+) Phantomroutine als Phantom",
            ClientLanguage.Japanese => @".+?」に(\d+)ポイントのサポート経験値を得た。",

            // 台服(繁中)：ClientLanguage 實測回報 7；5 是同一支 fork 裡的 ChineseTraditional。
            // 用數值轉型而不是列舉成員名，與 Plugin.cs 的語言對應、Ocelot 的 Localization.cs 同慣例
            // (上游 Dalamud 的列舉沒有 TraditionalChinese 這個名字)。
            // 🔴 這裡刻意回 null 而不是沿用下面的英文樣板：英文樣板在繁中用戶端永遠不會命中，
            //    是「看起來有支援、其實靜默失效」。台服 7.20 的遊戲資料尚未收錄新月島
            //    (2026-08-27 直讀台服 sqpack 實查：MKDSupportJob 13 列全空、
            //    LogMessage 最後一列有文字的是 11230，之後到 11350 全空)，
            //    因此無從離線取得逐字的繁中模板 —— 由上面的取樣器把真實訊息記進 log 再補。
            (ClientLanguage)5 or (ClientLanguage)7 => null,

            _ => @"You gain (\d+) Phantom .+? experience points\.",
        };
    }
}
