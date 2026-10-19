using System.Collections.Generic;
using Ocelot.Config.Attributes;
using Ocelot.Modules;

namespace BOCCHI.Modules.WindowManager;

public class WindowManagerConfig : ModuleConfig
{
    [Checkbox] public bool OpenMainOnStartUp { get; set; } = false;

    [Checkbox] public bool OpenMainOnEnter { get; set; } = true;

    [Checkbox] public bool CloseMainOnExit { get; set; } = true;

    [Checkbox] public bool HideMainInCombat { get; set; } = false;

    [Checkbox] public bool OpenConfigOnStartUp { get; set; } = false;

    [Checkbox] public bool OpenConfigOnEnter { get; set; } = false;

    [Checkbox] public bool CloseConfigOnExit { get; set; } = true;

    [Checkbox] public bool HideConfigInCombat { get; set; } = false;

    /// <summary>主視窗各區段由上到下的順序（模組型別全名）；空＝原本的順序。在設定頁的「主視窗區段順序」改，套用規則見 ModuleManager.SetMainOrder。</summary>
    public List<string> MainWindowOrder { get; set; } = [];
}
