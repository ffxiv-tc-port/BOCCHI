using System.Collections.Generic;
using System.Linq;
using BOCCHI.Modules.Automator;
using BOCCHI.Modules.StateManager;
using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using Ocelot;
using Ocelot.Modules;
using Ocelot.Ui;
using Ocelot.Windows;

namespace BOCCHI.Modules.WindowManager;

[OcelotModule(5)]
public class WindowManagerModule(Plugin _plugin, Config _config) : Module(_plugin, _config)
{
    public override WindowManagerConfig Config
    {
        get => PluginConfig.WindowManagerConfig;
    }

    public override bool ShouldInitialize
    {
        get => true;
    }


    private readonly List<uint> occultCrescentTerritoryIds = [1252];

    private bool mainClosed = false;

    private bool configClosed = false;


    public override void PostInitialize()
    {
        if (Config.OpenMainOnStartUp)
        {
            Plugin.Windows.OpenMainUI();
        }


        if (Config.OpenConfigOnStartUp)
        {
            Plugin.Windows.OpenConfigUI();
        }

        Svc.ClientState.TerritoryChanged += OnZoneChanged;

        GetModule<StateManagerModule>().OnEnterInCombat += EnterCombat;
        GetModule<StateManagerModule>().OnEnterInCriticalEncounter += EnterCombat;
        GetModule<StateManagerModule>().OnEnterInFate += EnterCombat;
        GetModule<StateManagerModule>().OnEnterIdle += ExitCombat;
    }

    // 刻意直接訂閱、不覆寫 OnTerritoryChanged：ModuleManager 只把換區轉給上一次在新月島裡更新過的模組，
    // 在島外載入插件時「進入時開啟」「離開時關閉」都收不到。跑在 framework 執行緒。
    private void OnZoneChanged(ushort id)
    {
        var mainWasOpen = Plugin.Windows.IsMainUIOpen();
        var configWasOpen = Plugin.Windows.IsConfigUIOpen();

        ApplyZoneRules(id);

        LogZoneToggle(id, "主介面", mainWasOpen, Plugin.Windows.IsMainUIOpen());
        LogZoneToggle(id, "設定介面", configWasOpen, Plugin.Windows.IsConfigUIOpen());
    }

    private static void LogZoneToggle(ushort id, string window, bool before, bool after)
    {
        if (before != after)
        {
            Svc.Log.Information($"[BOCCHI][視窗] 換區到 {id}：依「{(after ? "進入區域時開啟" : "離開區域時關閉")}{window}」{(after ? "開啟" : "關閉")}{window}");
        }
    }

    private void ApplyZoneRules(ushort id)
    {
        if (occultCrescentTerritoryIds.Contains(id))
        {
            if (Config.OpenMainOnEnter)
            {
                Plugin.Windows.OpenMainUI();
            }


            if (Config.OpenConfigOnEnter)
            {
                Plugin.Windows.OpenConfigUI();
            }
        }
        else
        {
            if (Config.CloseMainOnExit)
            {
                Plugin.Windows.CloseMainUI();
            }


            if (Config.CloseConfigOnExit)
            {
                Plugin.Windows.CloseConfigUI();
            }
        }
    }

    private void EnterCombat(StateManagerModule module)
    {
        if (Config.HideMainInCombat && Plugin.Windows.IsMainUIOpen())
        {
            Plugin.Windows.CloseMainUI();
            mainClosed = true;
        }

        if (Config.HideConfigInCombat && Plugin.Windows.IsConfigUIOpen())
        {
            Plugin.Windows.CloseConfigUI();
            configClosed = true;
        }
    }

    private void ExitCombat(StateManagerModule module)
    {
        if (Config.HideMainInCombat && mainClosed)
        {
            Plugin.Windows.OpenMainUI();
            mainClosed = false;
        }

        if (Config.HideConfigInCombat && configClosed)
        {
            Plugin.Windows.OpenConfigUI();
            configClosed = false;
        }
    }

    // 主視窗區段順序：只在畫面執行緒（主視窗、設定頁）讀寫。設定改了版本加一，下一次畫時交給 ModuleManager。
    private int orderVersion;

    private int appliedOrderVersion = -1;

    private IReadOnlyList<IModule>? sectionsFrom;

    private readonly List<IModule> sections = [];

    private readonly List<string> upIds = [];

    private readonly List<string> downIds = [];

    /// <summary>把設定裡的主視窗區段順序交給 ModuleManager（沒改過就什麼都不做）；只在畫面執行緒呼叫。</summary>
    public void ApplyMainOrder()
    {
        if (appliedOrderVersion == orderVersion)
        {
            return;
        }

        appliedOrderVersion = orderVersion;
        Plugin.Modules.SetMainOrder(Config.MainWindowOrder);
    }

    public override void RenderConfigUi(RenderContext context)
    {
        base.RenderConfigUi(context);
        DrawMainOrder();
    }

    // 用上／下按鈕排，不做拖曳（容易誤觸）；最上方的自動化控制不在清單裡、固定在最上面。
    private void DrawMainOrder()
    {
        ApplyMainOrder();
        RefreshSections();

        OcelotUi.VSpace();
        ImGui.TextColored(OcelotColor.Yellow, T("config.main_order.label"));
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(T("config.main_order.tooltip"));
        }

        ImGui.Indent(16);
        var from = -1;
        var to = -1;
        for (var i = 0; i < sections.Count; i++)
        {
            var first = i == 0;
            ImGui.BeginDisabled(first);
            if (ImGui.ArrowButton(upIds[i], ImGuiDir.Up))
            {
                (from, to) = (i, i - 1);
            }

            ImGui.EndDisabled();
            ImGui.SameLine();
            var last = i == sections.Count - 1;
            ImGui.BeginDisabled(last);
            if (ImGui.ArrowButton(downIds[i], ImGuiDir.Down))
            {
                (from, to) = (i, i + 1);
            }

            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextUnformatted(sections[i].T("panel.title"));
        }

        var custom = Config.MainWindowOrder.Count > 0;
        ImGui.BeginDisabled(!custom);
        var reset = ImGui.Button(T("config.main_order.reset"));
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(T("config.main_order.reset_tooltip"));
        }

        ImGui.Unindent(16);

        if (from >= 0)
        {
            var names = sections.Select(m => m.GetType().FullName!).ToList();
            (names[from], names[to]) = (names[to], names[from]);
            SaveMainOrder(names, "改成");
        }
        else if (reset && custom)
        {
            SaveMainOrder([], "恢復預設");
        }
    }

    private void SaveMainOrder(List<string> names, string what)
    {
        Config.MainWindowOrder = names;
        PluginConfig.Save();
        orderVersion++;
        ApplyMainOrder();
        RefreshSections();
        Svc.Log.Information($"[BOCCHI][視窗] 主視窗區段順序{what}：{string.Join("、", sections.Select(m => m.GetType().Name))}（設定頁）");
    }

    // 清單＝主視窗會畫區段的模組，順序同主視窗；ModuleManager 重排時換一個陣列，那時才重建。
    private void RefreshSections()
    {
        var order = Plugin.Modules.MainOrder;
        if (ReferenceEquals(order, sectionsFrom))
        {
            return;
        }

        sectionsFrom = order;
        sections.Clear();
        foreach (var module in order)
        {
            if (HasMainSection(module))
            {
                sections.Add(module);
            }
        }

        for (var i = upIds.Count; i < sections.Count; i++)
        {
            upIds.Add($"##main-order-up-{i}");
            downIds.Add($"##main-order-down-{i}");
        }
    }

    // 覆寫了 RenderMainUi 的模組；AutomatorModule 例外：它永遠回 false（目前活動畫在最上方的自動化控制）。
    private static bool HasMainSection(IModule module)
    {
        if (module is AutomatorModule)
        {
            return false;
        }

        var method = module.GetType().GetMethod(nameof(IModule.RenderMainUi), [typeof(RenderContext)]);
        return method != null && method.DeclaringType != typeof(Module<Plugin, Config>);
    }

    public override void Dispose()
    {
        base.Dispose();
        Svc.ClientState.TerritoryChanged -= OnZoneChanged;
        GetModule<StateManagerModule>().OnEnterInCombat -= EnterCombat;
        GetModule<StateManagerModule>().OnEnterInCriticalEncounter -= EnterCombat;
        GetModule<StateManagerModule>().OnEnterInFate -= EnterCombat;
        GetModule<StateManagerModule>().OnEnterIdle -= ExitCombat;
    }
}
