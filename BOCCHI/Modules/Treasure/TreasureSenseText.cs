using System;
using System.Text;
using Lumina.Text.Expressions;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

namespace BOCCHI.Modules.Treasure;

/// <summary>
/// 以遊戲表的字串當範本，從聊天文字讀出魔尋寶感知到的寶箱數：LogMessage <see cref="SenseRow"/>（銀 lnum1、銅 lnum2）
/// 與 <see cref="NoneRow"/>（沒有寶箱＝兩種都 0）。範本必須恰好是「文字、數字、文字、數字、文字」，數字的參數號碼決定哪個是銀。
/// 文字段逐字比對、整句都要對上；讀不到就回 false（呼叫端當作不知道）。刻意只依賴 Lumina，離線驗收工具直接用。
/// </summary>
public sealed class TreasureSenseText
{
    public const uint SenseRow = 10965;

    public const uint NoneRow = 10966;

    private readonly string prefix;

    private readonly string middle;

    private readonly string suffix;

    private readonly bool silverFirst;

    private readonly string? none;

    private TreasureSenseText(string prefix, string middle, string suffix, bool silverFirst, string? none)
    {
        this.prefix = prefix;
        this.middle = middle;
        this.suffix = suffix;
        this.silverFirst = silverFirst;
        this.none = none;
    }

    /// <summary>範本的三段文字（給記錄用）。</summary>
    public string Describe()
    {
        return $"「{prefix}」數字「{middle}」數字「{suffix}」{(silverFirst ? "（先銀後銅）" : "（先銅後銀）")}；沒有寶箱：{(none == null ? "讀不到" : $"「{none}」")}";
    }

    /// <returns>範本形狀不對（不是兩個 lnum1／lnum2 數字夾三段文字、或中間那段是空的）回 null。<paramref name="noneText"/> 有巨集時只停用「沒有寶箱」那句。</returns>
    public static TreasureSenseText? FromSeStrings(ReadOnlySeString senseText, ReadOnlySeString? noneText)
    {
        var parts = new string[3];
        var slots = new uint[2];
        var count = 0;
        var current = new StringBuilder();
        foreach (var payload in senseText)
        {
            if (payload.Type == ReadOnlySePayloadType.Text)
            {
                current.Append(Encoding.UTF8.GetString(payload.Body.Span));
                continue;
            }

            if (payload.Type != ReadOnlySePayloadType.Macro || payload.MacroCode != MacroCode.Num || count >= 2 || !TryGetLocalNumber(payload, out var slot))
            {
                return null;
            }

            parts[count] = current.ToString();
            slots[count++] = slot;
            current.Clear();
        }

        if (count != 2 || parts[1].Length == 0 || !((slots[0] == 1 && slots[1] == 2) || (slots[0] == 2 && slots[1] == 1)))
        {
            return null;
        }

        parts[2] = current.ToString();
        return new TreasureSenseText(parts[0], parts[1], parts[2], slots[0] == 1, noneText is { } n ? PlainText(n) : null);
    }

    /// <summary>純文字範本（離線驗收用）：<paramref name="silverFirst"/>＝第一個數字是銀。</summary>
    public static TreasureSenseText FromParts(string prefix, string middle, string suffix, bool silverFirst, string? none)
    {
        return new TreasureSenseText(prefix, middle, suffix, silverFirst, none);
    }

    /// <summary>這句是魔尋寶的結果就回 true 並給出兩種的數量（「沒有寶箱」那句＝0、0）。</summary>
    public bool TryParse(string? text, out int silver, out int bronze)
    {
        silver = 0;
        bronze = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (none != null && string.Equals(text.Trim(), none, StringComparison.Ordinal))
        {
            return true;
        }

        if (!TryParse(text.Trim(), prefix, middle, suffix, out var first, out var second))
        {
            return false;
        }

        silver = silverFirst ? first : second;
        bronze = silverFirst ? second : first;
        return true;
    }

    /// <summary>含範本中間那段文字（例：「個銀寶箱、」）：讀不到數字時給記錄判斷要不要提一句。</summary>
    public bool LooksLikeSense(string? text)
    {
        return !string.IsNullOrEmpty(text) && text.Contains(middle, StringComparison.Ordinal);
    }

    /// <summary>整句＝prefix＋數字＋middle＋數字＋suffix，數字是 1～4 位 ASCII 數字。</summary>
    public static bool TryParse(string text, string prefix, string middle, string suffix, out int first, out int second)
    {
        first = 0;
        second = 0;
        if (!text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var end = text.Length - suffix.Length;
        var position = prefix.Length;
        if (!TryReadNumber(text, ref position, end, out first) || position + middle.Length > end || string.CompareOrdinal(text, position, middle, 0, middle.Length) != 0)
        {
            return false;
        }

        position += middle.Length;
        return TryReadNumber(text, ref position, end, out second) && position == end;
    }

    private static bool TryReadNumber(string text, ref int position, int end, out int value)
    {
        value = 0;
        var start = position;
        while (position < end && position - start < 5 && text[position] is >= '0' and <= '9')
        {
            value = value * 10 + (text[position] - '0');
            position++;
        }

        return position > start && position - start <= 4;
    }

    private static bool TryGetLocalNumber(ReadOnlySePayload payload, out uint slot)
    {
        slot = 0;
        return payload.TryGetExpression(out var expression)
               && expression.TryGetParameterExpression(out var type, out var operand)
               && type == (byte)ExpressionType.LocalNumber
               && operand.TryGetUInt(out slot);
    }

    // 有任何巨集就不用（null）：那句應該是純文字。
    private static string? PlainText(ReadOnlySeString text)
    {
        var builder = new StringBuilder();
        foreach (var payload in text)
        {
            if (payload.Type != ReadOnlySePayloadType.Text)
            {
                return null;
            }

            builder.Append(Encoding.UTF8.GetString(payload.Body.Span));
        }

        return builder.Length > 0 ? builder.ToString() : null;
    }
}
