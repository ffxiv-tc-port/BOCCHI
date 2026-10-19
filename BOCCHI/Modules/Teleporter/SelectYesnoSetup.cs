using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Memory;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace BOCCHI.Modules.Teleporter;

/// <summary>確認框 PreSetup 當下的讀取，與「只按接手的那一扇」的查找。只在框架執行緒用。</summary>
/// <remarks>
/// YesAlready 只在確認框 PostSetup 判斷一次，而 Dalamud 在同一次 Setup 呼叫裡先跑完全部 PreSetup：
/// 在 PreSetup 認得才請它暫停＝它一定不碰這一扇；認不出就不暫停、它照常判斷。
/// setup 參數與視窗指標只在那一次事件呼叫裡讀，不留到事件之後。
/// </remarks>
public static unsafe class SelectYesnoSetup
{
    public const string AddonName = "SelectYesno";

    private const int MaxInstances = 8;

    /// <summary>setup 參數的 AtkValues[index]；陣列是空的或數量不夠回 false。</summary>
    public static bool TryGetSetupValue(AddonSetupArgs setup, int index, out AtkValue value)
    {
        value = default;
        if (setup.AtkValues == nint.Zero || index < 0 || index >= setup.AtkValueCount)
        {
            return false;
        }

        value = ((AtkValue*)setup.AtkValues)[index];
        return true;
    }

    /// <summary>確認框內文：setup 參數的 AtkValues[0]（型別要是字串、指標非空），讀不到再試視窗自己的；都讀不到回 null。</summary>
    public static string? ReadPrompt(AddonSetupArgs setup)
    {
        if (TryGetSetupValue(setup, 0, out var value)
            && (value.Type is ValueType.String or ValueType.String8 or ValueType.ManagedString)
            &&value.String.Value != null)
        {
            return MemoryHelper.ReadSeStringNullTerminated((nint)value.String.Value)?.GetText();
        }

        return GenericHelpers.GetAtkValueTextOrNull((AtkUnitBase*)setup.Addon.Address, 0);
    }

    /// <summary>舊版會自動按的那種確認框（AtkValues[7] 是整數 -1）；只用來寫紀錄。</summary>
    public static bool IsLegacyAutoYes(AddonSetupArgs setup)
    {
        return TryGetSetupValue(setup, 7, out var value) && value.Type == ValueType.Int && value.Int == -1;
    }

    /// <summary>找出位址等於 <paramref name="address"/>、而且就緒的那一扇確認框。位址只做等值比較，從不解參。</summary>
    public static bool TryGetReadyAt(nint address, out AtkUnitBase* addon)
    {
        addon = null;
        if (address == nint.Zero)
        {
            return false;
        }

        for (var i = 1; i <= MaxInstances; i++)
        {
            var candidate = Svc.GameGui.GetAddonByName<AtkUnitBase>(AddonName, i);
            if (candidate == null || (nint)candidate != address)
            {
                continue;
            }

            if (!GenericHelpers.IsAddonReady(candidate))
            {
                return false;
            }

            addon = candidate;
            return true;
        }

        return false;
    }
}
