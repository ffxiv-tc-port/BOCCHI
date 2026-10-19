using System;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Lumina.Excel.Sheets;

namespace BOCCHI.Modules.PhantomJobs;

/// <summary>13 個輔助職業的名稱、滿級與目前等級。只在 framework 執行緒使用。</summary>
/// <remarks>
/// 名稱與滿級取自 MKDSupportJob：這版 Lumina 還沒替欄位命名，Unknown0＝名稱、Unknown10＝LevelMax。
/// 等級每次從新月島狀態重讀、當場抄走，不保存原生指標。
/// </remarks>
public static class PhantomJobTable
{
    public const int Count = 13;

    private static byte[]? maxLevels;

    private static string[]? names;

    public static ReadOnlySpan<byte> MaxLevels
    {
        get
        {
            if (maxLevels == null)
            {
                Load();
            }

            return maxLevels;
        }
    }

    public static string GetName(int id)
    {
        if (names == null)
        {
            Load();
        }

        return id >= 0 && id < Count ? names![id] : $"#{id}";
    }

    /// <summary>把目前等級抄進 <paramref name="levels"/>（長度至少 <see cref="Count"/>）。</summary>
    /// <returns>目前的輔助職業編號；讀不到（不在新月島、讀取中、找不到遊戲函式）回 -1＝不知道。</returns>
    public static unsafe int ReadLevels(Span<byte> levels)
    {
        if (levels.Length < Count || PublicContentOccultCrescent.MemberFunctionPointers.GetState == null)
        {
            return -1;
        }

        var state = PublicContentOccultCrescent.GetState();
        if (state == null)
        {
            return -1;
        }

        var source = state->SupportJobLevels;
        for (var i = 0; i < Count && i < source.Length; i++)
        {
            levels[i] = source[i];
        }

        var current = state->CurrentSupportJob;
        return current < Count ? current : -1;
    }

    /// <summary>某個輔助職業目前的經驗值；讀不到回 null。</summary>
    public static unsafe uint? ReadExperience(int id)
    {
        if (id < 0 || id >= Count || PublicContentOccultCrescent.MemberFunctionPointers.GetState == null)
        {
            return null;
        }

        var state = PublicContentOccultCrescent.GetState();
        if (state == null)
        {
            return null;
        }

        var source = state->SupportJobExperience;
        return id < source.Length ? source[id] : null;
    }

    private static void Load()
    {
        var max = new byte[Count];
        var text = new string[Count];
        var sheet = Svc.Data.GetExcelSheet<MKDSupportJob>();
        for (var i = 0; i < Count; i++)
        {
            var row = sheet.GetRowOrDefault((uint)i);
            max[i] = row?.Unknown10 ?? 0;
            var name = row?.Unknown0.ExtractText();
            text[i] = string.IsNullOrEmpty(name) ? $"#{i}" : name;
        }

        maxLevels = max;
        names = text;
        Svc.Log.Information($"[BOCCHI][輔助職業] 滿級表（MKDSupportJob）：{string.Join("、", ZipNames(text, max))}");
    }

    private static string[] ZipNames(string[] text, byte[] max)
    {
        var result = new string[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            result[i] = $"{text[i]} {max[i]}";
        }

        return result;
    }
}
