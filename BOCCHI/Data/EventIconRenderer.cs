using System;
using System.Collections.Generic;
using System.Numerics;
using BOCCHI.Modules.EventDrop;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using Ocelot;

namespace BOCCHI.Data;

public struct EventIconRenderer
{
    /// <summary>外框邊長（像素）；圖示本身少 2。</summary>
    public const float DefaultIconSize = 50f;

    public static void Drops(EventData data, EventDropConfig config)
    {
        Drops(data, config, DefaultIconSize, false);
    }

    /// <param name="sameLine">第一個圖示接在同一列上一個項目後面；一個都沒畫時不動游標，不會留下懸空的 SameLine。</param>
    public static void Drops(EventData data, EventDropConfig config, float size, bool sameLine)
    {
        var rendered = sameLine;

        if (data.Demiatma != null && config.ShowDemiatmaDrops)
        {
            if (rendered)
            {
                ImGui.SameLine();
            }

            Demiatma(data, size);
            rendered = true;
        }

        if (data.Note != null && config.ShowNoteDrops)
        {
            if (rendered)
            {
                ImGui.SameLine();
            }

            Notes(data, size);
            rendered = true;
        }

        if (data.Soulshard != null && config.ShowSoulShardDrops)
        {
            if (rendered)
            {
                ImGui.SameLine();
            }

            SoulShard(data, size);
        }
    }

    public static unsafe void Demiatma(EventData data, float size = DefaultIconSize)
    {
        var itemData = Svc.Data.GetExcelSheet<Item>().GetRow((uint)data.Demiatma!);

        var count = InventoryManager.Instance()->GetInventoryItemCount(itemData.RowId);
        var needed = Math.Max(0, 3 - count);

        var border = needed > 0 ? new Vector4(0.3f, 0.85f, 0.39f, 1f) : new Vector4(0.95f, 0.26f, 0.21f, 1f);

        var demiatma = Svc.Texture.GetFromGameIcon(new GameIconLookup(itemData.Icon)).GetWrapOrEmpty();

        DrawIcon(demiatma, border, $"Demiatma_{itemData.RowId}_fate_{data.Id}", size);

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();

            var label = needed > 0
                ? I18N.T("generic.event_drop.needed", new Dictionary<string, string> { ["count"] = needed.ToString() })
                : I18N.T("generic.event_drop.not_needed", new Dictionary<string, string> { ["count"] = count.ToString() });

            ImGui.TextUnformatted($"{itemData.Name}: {label}");
            ImGui.EndTooltip();
        }
    }

    public static void Notes(EventData data, float size = DefaultIconSize)
    {
        var itemData = Svc.Data.GetExcelSheet<Item>().GetRow((uint)data.Note!);

        var notes = Svc.Texture.GetFromGameIcon(new GameIconLookup(itemData.Icon)).GetWrapOrEmpty();

        DrawIcon(notes, new Vector4(1f, 1f, 1f, 1f), $"Note_{itemData.RowId}_fate_{data.Id}", size);

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(itemData.Name.ToString());
            ImGui.EndTooltip();
        }
    }

    private static void SoulShard(EventData data, float size)
    {
        var itemData = Svc.Data.GetExcelSheet<Item>().GetRow((uint)data.Soulshard!);

        var soulshard = Svc.Texture.GetFromGameIcon(new GameIconLookup(itemData.Icon)).GetWrapOrEmpty();

        DrawIcon(soulshard, new Vector4(1f, 1f, 1f, 1f), $"SoulShard_{itemData.RowId}_ce_{data.Id}", size);

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(itemData.Name.ToString());
            ImGui.EndTooltip();
        }
    }

    public static void DrawIcon(IDalamudTextureWrap icon, Vector4 border, string id, float size = DefaultIconSize)
    {
        ImGui.PushStyleColor(ImGuiCol.Border, border);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        ImGui.BeginChild($"ImageBorder##{id}", new Vector2(size, size), true, ImGuiWindowFlags.NoScrollbar);

        ImGui.Image(icon.Handle, new Vector2(size - 2, size - 2));

        ImGui.EndChild();

        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }
}
