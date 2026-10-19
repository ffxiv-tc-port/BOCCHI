using Dalamud.Bindings.ImGui;
using Ocelot.Ui;

namespace BOCCHI.Modules.Debug.Panels;

public abstract class Panel
{
    private const string HuntDataRegenTooltip =
        "Disabled on purpose.\n"
        + "In game, the path queries are fired without waiting for them, so the file is saved before they finish and comes out incomplete.\n"
        + "The node -> aethernet table is also filled with aethernet -> node paths (reverse direction).\n"
        + "Regenerate the cost tables offline with ~/.claude/tools/dotnet/bocchi_hunt_data_regen/ (only missing or moved entries are recomputed).";

    public abstract string GetName();

    public virtual void Update(DebugModule module)
    {
    }

    public virtual void Render(DebugModule module)
    {
    }

    public virtual void OnTerritoryChanged(ushort id, DebugModule module)
    {
    }

    // 巡迴成本表的遊戲內重算停用（會截斷、節點→乙太之晶的方向相反），只畫停用的按鈕與說明。
    protected static void DrawHuntDataRegenDisabled()
    {
        ImGui.BeginDisabled();
        ImGui.Button("Run");
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(HuntDataRegenTooltip);
        }

        OcelotUi.Error("Run is disabled: it saves an incomplete table, and node -> aethernet paths are reversed.");
        ImGui.TextUnformatted("Regenerate offline: ~/.claude/tools/dotnet/bocchi_hunt_data_regen/");
    }
}
