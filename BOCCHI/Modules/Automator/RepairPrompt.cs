using System.Collections.Generic;
using BOCCHI.Modules.Teleporter;
using Lumina.Text.ReadOnly;

namespace BOCCHI.Modules.Automator;

/// <summary>判斷確認框（SelectYesno AtkValue 0）是不是「自己修理全部裝備」的確認句。刻意只依賴 Lumina，離線驗收直接編譯這個檔。</summary>
/// <remarks>
/// Addon#863＝「要修理目前所有裝備嗎？」＋消耗清單（自己修理）；#861 開頭相同但接「費用」，是找 NPC 修理的確認句，要排除。
/// 自己修理的範本讀不到時一律回 false（不代按）。
/// </remarks>
public sealed class RepairPrompt
{
    public const uint SelfRepairRow = 863;

    public const uint MenderRow = 861;

    private readonly ReturnPromptMatcher? self;

    private readonly ReturnPromptMatcher? mender;

    public RepairPrompt(ReadOnlySeString? selfTemplate, ReadOnlySeString? menderTemplate)
    {
        self = selfTemplate is { } s ? ReturnPromptMatcher.FromSeString(s) : null;
        mender = menderTemplate is { } m ? ReturnPromptMatcher.FromSeString(m) : null;
    }

    public bool IsAvailable
    {
        get => self != null;
    }

    public IReadOnlyList<string> Segments
    {
        get => self?.Segments ?? [];
    }

    public bool IsSelfRepairPrompt(string? text)
    {
        return self != null && self.IsMatch(text) && mender?.IsMatch(text) != true;
    }
}
