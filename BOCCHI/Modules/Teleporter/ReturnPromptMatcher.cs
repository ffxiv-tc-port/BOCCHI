using System;
using System.Collections.Generic;
using System.Text;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

namespace BOCCHI.Modules.Teleporter;

/// <summary>
/// 以遊戲表的字串當範本，比對確認框（SelectYesno AtkValue 0）上的畫面文字。
/// </summary>
/// <remarks>
/// 文字段逐字比對；換行巨集與畫面上的字面換行兩邊都去掉；其餘巨集（地名等代入文字）當成任意長度的空位。
/// 刻意只依賴 Lumina，離線驗收工具直接編譯這個檔。
/// </remarks>
public sealed class ReturnPromptMatcher
{
    private readonly string[] segments;

    private ReturnPromptMatcher(string[] segments)
    {
        this.segments = segments;
    }

    /// <summary>範本的文字段（依序），給 log 用。</summary>
    public IReadOnlyList<string> Segments
    {
        get => segments;
    }

    /// <returns>範本沒有任何文字（全是巨集或空字串）時回 null：那種範本會比中任何句子，不能拿來判斷。</returns>
    public static ReturnPromptMatcher? FromSeString(ReadOnlySeString text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();

        foreach (var payload in text)
        {
            if (payload.Type == ReadOnlySePayloadType.Text)
            {
                current.Append(Encoding.UTF8.GetString(payload.Body.Span));
                continue;
            }

            if (payload.Type != ReadOnlySePayloadType.Macro || payload.MacroCode == MacroCode.NewLine)
            {
                continue;
            }

            parts.Add(Normalize(current.ToString()));
            current.Clear();
        }

        parts.Add(Normalize(current.ToString()));

        return parts.Exists(p => p.Length > 0) ? new ReturnPromptMatcher(parts.ToArray()) : null;
    }

    public bool IsMatch(string? screenText)
    {
        if (string.IsNullOrEmpty(screenText))
        {
            return false;
        }

        var text = Normalize(screenText);
        var first = segments[0];
        if (!text.StartsWith(first, StringComparison.Ordinal))
        {
            return false;
        }

        if (segments.Length == 1)
        {
            return text.Length == first.Length;
        }

        var position = first.Length;
        for (var i = 1; i < segments.Length - 1; i++)
        {
            var index = text.IndexOf(segments[i], position, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            position = index + segments[i].Length;
        }

        var last = segments[^1];
        return text.Length - last.Length >= position && text.EndsWith(last, StringComparison.Ordinal);
    }

    private static string Normalize(string value)
    {
        return value.ReplaceLineEndings(string.Empty);
    }
}
