using System.Numerics;
using BOCCHI.Automation;
using BOCCHI.Data;
using BOCCHI.Modules.Automator;
using BOCCHI.Modules.WindowManager;
using Dalamud.Interface;
using Dalamud.Bindings.ImGui;
using Ocelot;
using Ocelot.Ui;
using Ocelot.Windows;

namespace BOCCHI.Windows;

[OcelotMainWindow]
public class MainWindow(Plugin primaryPlugin, Config config) : OcelotMainWindow(primaryPlugin, config)
{
    public override void PostInitialize()
    {
        base.PostInitialize();

        TitleBarButtons.Add(new TitleBarButton
        {
            Click = (m) =>
            {
                if (m != ImGuiMouseButton.Left)
                {
                    return;
                }

                ((Plugin)Plugin).Automation.StopAll(I18N.T("automation.source.title_button"), false);
            },
            Icon = FontAwesomeIcon.Stop,
            IconOffset = new Vector2(2, 2),
            ShowTooltip = () => ImGui.SetTooltip(I18N.T("windows.main.buttons.emergency_stop")),
        });

        TitleBarButtons.Add(new TitleBarButton
        {
            Click = (m) =>
            {
                if (m != ImGuiMouseButton.Left)
                {
                    return;
                }

                AutomatorModule.ToggleIllegalMode(Plugin);
            },
            Icon = FontAwesomeIcon.Skull,
            IconOffset = new Vector2(2, 2),
            ShowTooltip = () => ImGui.SetTooltip(I18N.T("windows.main.buttons.toggle_illegal_mode")),
        });
    }

    private readonly AutomationPanel automationPanel = new();

    protected override void Render(RenderContext context)
    {
        var automation = ((Plugin)Plugin).Automation;
        if (!ZoneData.IsInOccultCrescent())
        {
            ImGui.TextUnformatted(I18N.T("generic.label.not_in_zone"));

            // 島外照理不會有在跑的；萬一有，停止鈕要找得到。
            if (automation.IsAnyRunning())
            {
                automationPanel.Draw(automation);
            }

            return;
        }

        automationPanel.Draw(automation);
        OcelotUi.Separator();
        WindowManager?.ApplyMainOrder();
        Plugin.Modules.RenderMainUi(context);
    }

    private WindowManagerModule? windowManager;

    private bool windowManagerResolved;

    // 第一次畫時取一次：取不到（模組沒載入）時照原本的順序畫。
    private WindowManagerModule? WindowManager
    {
        get
        {
            if (!windowManagerResolved)
            {
                windowManagerResolved = true;
                Plugin.Modules.TryGetModule(out windowManager);
            }

            return windowManager;
        }
    }
}
