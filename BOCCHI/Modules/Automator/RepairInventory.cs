using System;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace BOCCHI.Modules.Automator;

/// <summary>身上裝備的耐久度與「能不能自己修」。只在 framework 執行緒使用；每次重讀，不保存原生指標。</summary>
/// <remarks>自己修的條件照 Artisan／AutoDuty：資料表有修理職業、有同級以上的黑暗物質、修理職業等級 ≥ 裝備等級 − 10。</remarks>
public static class RepairInventory
{
    public readonly record struct Scan(int LowestPercent, int Below, int SelfRepairableBelow, string? Reason);

    /// <summary>掃身上裝備；讀不到裝備欄（換區、剛登入）回 false＝不知道。</summary>
    /// <remarks>只算資料表上可以修理的裝備；一件都沒有時 LowestPercent 是 100。</remarks>
    public static unsafe bool TryScan(int threshold, out Scan scan)
    {
        scan = default;
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            return false;
        }

        var container = manager->GetInventoryContainer(InventoryType.EquippedItems);
        if (container == null || !container->IsLoaded || container->Items == null || container->Size <= 0)
        {
            return false;
        }

        var items = Svc.Data.GetExcelSheet<Item>();
        var lowest = 100;
        var below = 0;
        var repairable = 0;
        string? reason = null;
        for (var i = 0; i < container->Size; i++)
        {
            var slot = container->Items + i;
            if (slot->ItemId == 0 || items.GetRowOrDefault(slot->ItemId) is not { } row || row.ClassJobRepair.RowId == 0)
            {
                continue;
            }

            var percent = slot->Condition / 300;
            lowest = Math.Min(lowest, percent);
            if (percent > threshold)
            {
                continue;
            }

            below++;
            if (CanSelfRepair(manager, row, out var why))
            {
                repairable++;
            }
            else
            {
                reason ??= why;
            }
        }

        scan = new Scan(lowest, below, repairable, reason);
        return true;
    }

    private static unsafe bool CanSelfRepair(InventoryManager* manager, Item item, out string why)
    {
        var material = item.ItemRepair.ValueNullable?.Item.RowId ?? 0;
        if (material == 0)
        {
            why = "資料表上沒有修理材料";
            return false;
        }

        if (!HasDarkMatterOrBetter(manager, material))
        {
            why = "沒有夠等級的黑暗物質";
            return false;
        }

        var job = item.ClassJobRepair.ValueNullable;
        var levels = PlayerState.Instance()->ClassJobLevels;
        var index = job?.ExpArrayIndex ?? -1;
        if (index < 0 || index >= levels.Length || levels[index] < Math.Max(item.LevelEquip - 10, 1))
        {
            why = "修理職業等級不足";
            return false;
        }

        why = string.Empty;
        return true;
    }

    private static unsafe bool HasDarkMatterOrBetter(InventoryManager* manager, uint material)
    {
        foreach (var resource in Svc.Data.GetExcelSheet<ItemRepairResource>())
        {
            var id = resource.Item.RowId;
            if (id >= material && manager->GetInventoryItemCount(id) > 0)
            {
                return true;
            }
        }

        return false;
    }
}
