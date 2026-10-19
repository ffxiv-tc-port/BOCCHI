using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BOCCHI.Data;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;

namespace BOCCHI.Modules.Treasure;

/// <summary>讀目前區域版面裡的寶箱生成點（模型是青銅或白銀寶箱的 Treasure 列）。只在框架執行緒呼叫，指標不留到呼叫結束之後。</summary>
public static class TreasureLayout
{
    public const uint BronzeModel = 1596;

    public const uint SilverModel = 1597;

    /// <returns>false＝版面還沒就緒（區域切換、讀圖中），<paramref name="why"/> 是給記錄的原因，<paramref name="into"/> 不動。</returns>
    public static unsafe bool TryRead(List<TreasureData.TreasureDatum> into, out string why)
    {
        // LayoutWorld 是 isPointer 的靜態位址，Instance() 回的是槽裡的值，區域切換期間可以是 null。
        var layoutWorld = LayoutWorld.Instance();
        if (layoutWorld == null)
        {
            why = "No layout world";
            return false;
        }

        var layout = layoutWorld->ActiveLayout;
        if (layout == null)
        {
            why = "No active layout";
            return false;
        }

        if (!layout->InstancesByType.TryGetValue(InstanceType.Treasure, out var mapPtr, false))
        {
            why = "No active treasure map";
            return false;
        }

        var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Treasure>();
        foreach (ILayoutInstance* instance in mapPtr.Value->Values)
        {
            var transform = instance->GetTransformImpl();
            var position = transform->Translation;
            if (position.Y <= -10f)
            {
                continue;
            }

            var treasureRowId = Unsafe.Read<uint>((byte*)instance + 0x30);
            if (sheet.GetRowOrDefault(treasureRowId) is not { } row)
            {
                continue;
            }

            var sgbId = row.SGB.RowId;
            if (sgbId != BronzeModel && sgbId != SilverModel)
            {
                continue;
            }

            into.Add(new TreasureData.TreasureDatum(treasureRowId, position, sgbId));
        }

        why = string.Empty;
        return true;
    }
}
