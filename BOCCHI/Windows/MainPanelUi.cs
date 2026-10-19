using System.Numerics;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.EventDrop;
using BOCCHI.Modules.Teleporter;
using Dalamud.Bindings.ImGui;
using Ocelot;
using Ocelot.Modules;

namespace BOCCHI.Windows;

/// <summary>主視窗各區段共用：標題與「標籤: 值」排在同一列。文字一律走 ImU8String，不產生字串。</summary>
public static class MainPanelUi
{
    /// <summary>黃色區段標題（自動補冒號）。同一列後面接按鈕時傳 alignToFrame，文字才會與按鈕對齊。</summary>
    public static void Title(string title, bool alignToFrame = false)
    {
        if (alignToFrame)
        {
            ImGui.AlignTextToFramePadding();
        }

        ImGui.TextColored(OcelotColor.Yellow, $"{title}:");
    }

    /// <summary>「標籤: 值」；之後的 IsItemHovered 判的是值那一段。</summary>
    public static void Labelled(string label, ImU8String value)
    {
        ImGui.TextColored(OcelotColor.Yellow, $"{label}:");
        ImGui.SameLine();
        ImGui.TextUnformatted(value);
    }

    /// <summary>同一列接下一組資訊，前面空一個字寬。</summary>
    public static void Gap()
    {
        ImGui.SameLine(0, ImGui.GetFontSize());
    }

    /// <summary>
    /// F.A.T.E.／緊急遭遇戰一場的第二列：尋路／傳送按鈕與掉落圖示同一列，圖示縮成按鈕高。
    /// 沒畫按鈕時（withButtons 為 false，或傳送模組、vnavmesh 沒就緒），圖示照原本縮排自成一列。
    /// </summary>
    public static void EventActions(Module<Plugin, Config> module, bool withButtons, Aethernet? aethernet, Vector3 destination, string name, string id, EventData data)
    {
        var drops = module.PluginConfig.EventDropConfig;
        var size = ImGui.GetFrameHeight();
        if (withButtons
            && module.TryGetModule<TeleporterModule>(out var teleporter)
            && teleporter!.IsReady()
            && teleporter.teleporter.Button(aethernet, destination, name, id, data))
        {
            EventIconRenderer.Drops(data, drops, size, true);
            return;
        }

        ImGui.Indent(16);
        EventIconRenderer.Drops(data, drops, size, false);
        ImGui.Unindent(16);
    }
}
