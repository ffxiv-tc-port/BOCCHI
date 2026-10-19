using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BOCCHI.Modules;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using Ocelot.Modules;
using Ocelot.Windows;

namespace BOCCHI.Windows;

[OcelotConfigWindow]
public class ConfigWindow(Plugin primaryPlugin, Config config) : OcelotConfigWindow(primaryPlugin, config)
{
    private IModule? selectedConfigModule;

    public override void PostInitialize()
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 0),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    // 模組在外掛初始化時就登記完、之後不增減：排序好的清單留著用，不必每幀重排。
    private List<IModule>? orderedModules;

    protected override void Render(RenderContext context)
    {
        var modules = orderedModules ??= Plugin.Modules.GetModulesByConfigOrder().ToList();
        selectedConfigModule ??= modules.FirstOrDefault();

        using (ImRaii.Child("##LeftPanel", new Vector2(300, 0), true))
        {
            foreach (var module in modules)
            {
                if (module is not Module concreteModule || concreteModule.Config == null)
                {
                    continue;
                }

                var name = concreteModule.Config.GetType().Name;

                var title = concreteModule.Config.GetTitle();
                if (title != null)
                {
                    name = title;
                }

                var selected = module == selectedConfigModule;
                if (ImGui.Selectable(name, selected))
                {
                    selectedConfigModule = module;
                }
            }
        }

        ImGui.SameLine();


        using (ImRaii.Child("##RightPanel", new Vector2(0, 0), true))
        {
            selectedConfigModule!.RenderConfigUi(context);
        }
    }
}
