using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using Ocelot;
using ExcelAction = Lumina.Excel.Sheets.Action;

namespace BOCCHI.Data;

// 翻譯檔的值可以寫「{表:列|後備文字}」，載入時換成遊戲表裡的名稱；列不存在或名稱是空的就用後備文字。
// 表：ce＝DynamicEvent、fate＝Fate、status＝Status、action＝Action、item＝Item、
// supportjob＝MKDSupportJob、place＝PlaceName、generalaction＝GeneralAction。
internal static partial class GameTextTokens
{
    [GeneratedRegex(@"\{(ce|fate|status|action|item|supportjob|place|generalaction):(\d+)\|([^{}]*)\}")]
    private static partial Regex TokenPattern();

    /// <summary>讀某語言目錄下的翻譯檔，把含記號的值解析後覆蓋回 I18N（要在 LoadAllFromDirectory 之後呼叫）。</summary>
    /// <remarks>絕不往外擲：這支在插件建構子裡跑，擲出去整個插件就載不起來。</remarks>
    public static void Apply(string language, string directory)
    {
        try
        {
            ApplyCore(language, directory);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[BOCCHI][翻譯] 套用遊戲表名稱失敗，介面上可能看到原始記號：{ex.Message}");
        }
    }

    private static void ApplyCore(string language, string directory)
    {
        if (!Directory.Exists(directory))
        {
            Svc.Log.Warning($"[BOCCHI][翻譯] 找不到翻譯目錄 {directory}，遊戲表名稱沒有套用。");
            return;
        }

        var resolved = new Dictionary<string, string>();
        var fromSheet = 0;
        var fallbacks = new List<string>();

        foreach (var file in Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            JsonObject? root;
            try
            {
                root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"[BOCCHI][翻譯] 讀不到 {Path.GetFileName(file)}：{ex.Message}");
                continue;
            }

            if (root != null)
            {
                Collect(root, string.Empty, resolved, ref fromSheet, fallbacks);
            }
        }

        if (resolved.Count == 0)
        {
            return;
        }

        I18N.AddTranslations(language, resolved);
        Svc.Log.Information($"[BOCCHI][翻譯] {language} 的遊戲名稱：{fromSheet} 處取自遊戲表，{fallbacks.Count} 處用翻譯檔內建文字"
                            + (fallbacks.Count > 0 ? $"（{string.Join("、", fallbacks)}）" : string.Empty));
    }

    private static void Collect(JsonObject obj, string prefix, Dictionary<string, string> resolved, ref int fromSheet, List<string> fallbacks)
    {
        foreach (var (name, node) in obj)
        {
            var key = prefix.Length == 0 ? name : $"{prefix}.{name}";
            if (node is JsonObject child)
            {
                Collect(child, key, resolved, ref fromSheet, fallbacks);
                continue;
            }

            if (node is not JsonValue value || !value.TryGetValue<string>(out var text) || !TokenPattern().IsMatch(text))
            {
                continue;
            }

            var sheetHits = 0;
            resolved[key] = TokenPattern().Replace(text, match =>
            {
                var sheet = match.Groups[1].Value;
                var id = uint.Parse(match.Groups[2].Value);
                var fromTable = Lookup(sheet, id);
                if (string.IsNullOrWhiteSpace(fromTable))
                {
                    fallbacks.Add($"{sheet}:{id}");
                    return match.Groups[3].Value;
                }

                sheetHits++;
                return fromTable;
            });
            fromSheet += sheetHits;
        }
    }

    private static string? Lookup(string sheet, uint id)
    {
        try
        {
            return sheet switch
            {
                "ce" => Svc.Data.GetExcelSheet<DynamicEvent>().GetRowOrDefault(id)?.Name.ExtractText(),
                "fate" => Svc.Data.GetExcelSheet<Fate>().GetRowOrDefault(id)?.Name.ExtractText(),
                "status" => Svc.Data.GetExcelSheet<Status>().GetRowOrDefault(id)?.Name.ExtractText(),
                "action" => Svc.Data.GetExcelSheet<ExcelAction>().GetRowOrDefault(id)?.Name.ExtractText(),
                "item" => Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(id)?.Name.ExtractText(),
                // 這版 Lumina 還沒替 MKDSupportJob 命名欄位；Unknown0 是職業名。
                "supportjob" => Svc.Data.GetExcelSheet<MKDSupportJob>().GetRowOrDefault(id)?.Unknown0.ExtractText(),
                "place" => Svc.Data.GetExcelSheet<PlaceName>().GetRowOrDefault(id)?.Name.ExtractText(),
                "generalaction" => Svc.Data.GetExcelSheet<GeneralAction>().GetRowOrDefault(id)?.Name.ExtractText(),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[BOCCHI][翻譯] 查 {sheet}:{id} 失敗：{ex.Message}");
            return null;
        }
    }
}
