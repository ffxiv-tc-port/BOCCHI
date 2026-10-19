using System;

namespace BOCCHI.Modules.Automator;

public enum AiType
{
    VBM, // Boss Mod
    BMR, // Bossmod Reborn
}

public static class AiProviderExtensions
{
    public static string ToLabel(this AiType provider)
    {
        return provider switch
        {
            AiType.VBM => "Boss Mod by veyn, xan_0",
            AiType.BMR => "BossMod Reborn by The Combat Reborn team",
            _ => provider.ToString(),
        };
    }

    /// <summary>開／關該 AI 的完整聊天指令；由 <see cref="AutomatorModule.SetAi"/> 以 Dalamud 指令處理送出。</summary>
    public static string Command(this AiType provider, bool on)
    {
        var state = on ? "on" : "off";
        return provider switch
        {
            AiType.VBM => $"/vbmai {state}",
            AiType.BMR => $"/bmrai {state}",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };
    }
}
