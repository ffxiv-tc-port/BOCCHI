using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace BOCCHI.Modules.Aggro;

// 本檔假設與契約：只在框架執行緒執行（自己掛 Framework.Update：BOCCHI 在過場、讀取、離開新月島時不更新模組，那時也要能切回跑步）；
// 唯一的例外是送鍵：丟到執行緒池用 SendMessage 送（同 SND 的 /send），由遊戲主執行緒在訊息迴圈裡處理，不在框架更新中途重入視窗程序。
// 切換＝替你按一下遊戲的「切換奔跑與行走」鍵位（InputId WALK）：遊戲沒有 /walk 指令，也沒有能單獨呼叫的切換函式；不寫遊戲記憶體。
// 讀的原生資料（Control.IsWalking、鍵位設定）都在同一拍讀完抄成值，不跨幀保存指標。
// 要求（WalkRequest）由 AggroModule.Update 每拍送進來；超過 RequestMaxAgeMs 沒更新就當成不要走路。

/// <summary>走路穿越的執行端：讀遊戲現在是不是走路、照 <see cref="WalkToggle"/> 的決定按鍵、寫 log。</summary>
public sealed unsafe class WalkThrough : IDisposable
{
    public const long RequestMaxAgeMs = 300;

    private const long KeybindRefreshMs = 1_000;

    /// <summary>按下後隔這麼久才放開：同時按下放開，遊戲可能讀不到「按下」。</summary>
    private const int HoldMs = 50;

    private static readonly ConditionFlag[] BlockFlags =
    [
        ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51, ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.WatchingCutscene,
        ConditionFlag.WatchingCutscene78, ConditionFlag.OccupiedInEvent, ConditionFlag.OccupiedInQuestEvent, ConditionFlag.Mounting, ConditionFlag.Mounting71,
    ];

    public readonly WalkToggle Toggle = new();

    private readonly HashSet<ulong> logged = new(16);

    private WalkRequest request;

    private bool hasRequest;

    private long requestAt;

    private WalkRequest sentFor;

    private WalkStopReason sentReason;

    // 送鍵中（按下到放開）；完成前不送下一次。
    private Task? pressTask;

    private bool keybindRead;

    private long keybindReadAt;

    private bool keyUsable;

    private bool keyUsableLogged;

    private byte key;

    private byte modifiers;

    private bool broken;

    public WalkThrough()
    {
        Svc.Framework.Update += Tick;
    }

    /// <summary>最近一次讀到的遊戲狀態：是不是走路。</summary>
    public bool LastWalking { get; private set; }

    /// <summary>讀過鍵位、而且沒有能用的鍵盤按鍵（面板顯示用；還沒讀過時為 false）。</summary>
    public bool KeyMissing
    {
        get => keybindRead && !keyUsable;
    }

    public void Request(long now, in WalkRequest value)
    {
        request = value;
        requestAt = now;
        hasRequest = true;
    }

    /// <summary>這一趟能不能走路穿越（能才把聽覺範圍從繞路裡拿掉）：讀得到狀態、有能用的按鍵、沒在放棄期間、你沒自己切回跑步。</summary>
    public bool Available(long now)
    {
        return !broken && RefreshKeybind(now) && Toggle.Available(now);
    }

    public void Dispose()
    {
        Svc.Framework.Update -= Tick;

        // 卸載後沒有下一拍可以確認：只在框架執行緒、遊戲沒在關閉時按一次，不等結果（送鍵 50 ms，遠短於 Dalamud 釋放插件前的等待）。
        if (!WalkThroughRules.ShouldRestoreOnDispose(Toggle.MayOwn, Svc.Framework.IsFrameworkUnloading, Svc.Framework.IsInFrameworkUpdateThread, keyUsable)
            || !TryReadWalking(out var walking) || !walking)
        {
            return;
        }

        Observe(Press(key, Modifiers(modifiers)));
        Log($"插件停用時你還在走路（是 BOCCHI 切的）：已按一下「切換奔跑與行走」（{KeyName(key, modifiers)}）切回跑步；沒有下一拍可以確認，沒切回的話請自己按一下");
    }

    private void Tick(IFramework framework)
    {
        var now = Environment.TickCount64;
        if (pressTask is { IsCompleted: true })
        {
            if (pressTask.IsFaulted)
            {
                var error = pressTask.Exception?.GetBaseException();
                Log($"送鍵失敗（{error?.GetType().Name}：{error?.Message}）");
            }

            pressTask = null;
        }

        var want = WalkThroughRules.EffectiveWant(hasRequest, requestAt, now, RequestMaxAgeMs, request, out var reason);
        if (!want && !Toggle.Owned && !Toggle.Pending)
        {
            // 沒在管走路：不讀遊戲狀態，只讓狀態機收掉「這一段已報過」之類的記號。
            Toggle.Step(now, false, false, false);
            return;
        }

        if (!TryReadWalking(out var walking))
        {
            return;
        }

        LastWalking = walking;
        var send = Toggle.Step(now, want, walking, pressTask == null && CanSend(now));
        Report(walking, reason);
        if (want && Toggle.Owned && walking && logged.Add(request.ZoneObjectId))
        {
            Log($"繼續走路經過 {Describe(request)}{RideText()}");
        }

        if (send == WalkSend.None)
        {
            return;
        }

        sentFor = request;
        sentReason = reason;
        pressTask = Press(key, Modifiers(modifiers));
    }

    private void Report(bool walking, WalkStopReason reason)
    {
        switch (Toggle.Event)
        {
            case WalkEvent.Walking:
                logged.Clear();
                logged.Add(sentFor.ZoneObjectId);
                Log($"{Describe(sentFor)}：已切成走路{RideText()}（按「切換奔跑與行走」{KeyName(key, modifiers)}），穿過後切回跑步");
                break;
            case WalkEvent.Restored:
                logged.Clear();
                Log($"已切回跑步{RideText()}（{ReasonText(sentReason)}）");
                break;
            case WalkEvent.AlreadyWalking:
                Log($"{Describe(request)}：你本來就在走路{RideText()}，不切換；離開時也不切回跑步");
                break;
            case WalkEvent.UserRan:
                logged.Clear();
                Log("走路途中你自己切回跑步：這一趟不再切成走路，聽覺範圍改回照舊繞開");
                break;
            case WalkEvent.UserRestored:
                logged.Clear();
                Log($"該切回跑步時已經是跑步了（你自己切的或遊戲切的），不用再按（{ReasonText(reason)}）");
                break;
            case WalkEvent.GaveUp:
                var what = Toggle.GaveUpOnRun ? "切回跑步" : "切成走路";
                var then = Toggle.GaveUpOnRun ? "還在走路的話請自己按一下切回跑步，之後會再試" : "這段時間聽覺範圍照舊繞開";
                Log($"按了 {WalkToggle.MaxAttempts} 次「切換奔跑與行走」（{KeyName(key, modifiers)}）都沒{what}（每次等 {WalkToggle.VerifyMs / 1000} 秒確認，"
                    + $"目前{(walking ? "走路" : "跑步")}），{WalkToggle.GiveUpMs / 1000} 秒內不再按；{then}");
                break;
        }
    }

    private bool CanSend(long now)
    {
        if (Svc.Framework.IsFrameworkUnloading || !RefreshKeybind(now) || Svc.Objects.LocalPlayer == null)
        {
            return false;
        }

        foreach (var flag in BlockFlags)
        {
            if (Svc.Condition[flag])
            {
                return false;
            }
        }

        // 你正按著修飾鍵或這個鍵時不送：會組成別的鍵位。
        var keys = Svc.KeyState;
        if (keys[VirtualKey.SHIFT] || keys[VirtualKey.CONTROL] || keys[VirtualKey.MENU] || (keys.IsVirtualKeyValid(key) && keys[(VirtualKey)key]))
        {
            return false;
        }

        // 打字中不送：遊戲或插件視窗的文字框可能會吃掉這個鍵。
        return !ImGui.GetIO().WantTextInput && !TextInputActive();
    }

    private static bool TextInputActive()
    {
        if (AtkModule.MemberFunctionPointers.IsTextInputActive == null)
        {
            return true;
        }

        var module = RaptureAtkModule.Instance();
        return module == null || module->AtkModule.IsTextInputActive();
    }

    private bool TryReadWalking(out bool walking)
    {
        walking = false;
        if (broken)
        {
            return false;
        }

        try
        {
            var control = Control.Instance();
            if (control == null)
            {
                return false;
            }

            walking = control->IsWalking;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            broken = true;
            Log($"讀不到遊戲現在是走路還是跑步（{ex.Message}）：走路穿越停用，聽覺範圍照舊繞開");
            return false;
        }
    }

    // 回傳有沒有能用的按鍵；每秒最多讀一次（你可能在遊戲裡改鍵位）。
    private bool RefreshKeybind(long now)
    {
        if (keybindRead && now - keybindReadAt < KeybindRefreshMs)
        {
            return keyUsable;
        }

        keybindRead = true;
        keybindReadAt = now;
        var usable = false;
        if (InputData.MemberFunctionPointers.GetKeybind != null)
        {
            var input = UIInputData.Instance();
            // GetKeybind 只做 Keybinds + id×0xB、不檢查邊界：鍵位表還沒配置或比 WALK 短時不呼叫。
            var bind = input == null || input->InputData.Keybinds == null || input->InputData.NumKeybinds <= (int)InputId.WALK
                ? null
                : input->GetKeybind(InputId.WALK);
            if (bind != null)
            {
                var first = bind->KeySettings[0];
                var second = bind->KeySettings[1];
                usable = WalkThroughRules.TryChooseKey((byte)first.Key, (byte)first.KeyModifier, (byte)second.Key, (byte)second.KeyModifier, out key, out modifiers);
            }
        }

        if (usable != keyUsable || !keyUsableLogged)
        {
            keyUsableLogged = true;
            Log(usable
                ? $"找到遊戲的「切換奔跑與行走」按鍵 {KeyName(key, modifiers)}：聽覺範圍改用走路穿越"
                : "遊戲的「切換奔跑與行走」沒有設定能用的鍵盤按鍵（沒設、滑鼠鍵、單按修飾鍵或移動鍵都不能用）：走路穿越無法使用，聽覺範圍照舊繞開");
        }

        keyUsable = usable;
        return usable;
    }

    private static Task Press(byte key, int[]? modifiers)
    {
        return WalkKeyPress.Run(key, modifiers, HoldMs);
    }

    // 卸載時丟出去就不管：之後才失敗也要讀一次 Exception，免得 Dalamud 記「Unobserved exception in Task」。
    private static void Observe(Task task)
    {
        task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static int[]? Modifiers(byte flags)
    {
        if (flags == 0)
        {
            return null;
        }

        var list = new List<int>(3);
        if ((flags & 2) != 0)
        {
            list.Add((int)VirtualKey.CONTROL);
        }

        if ((flags & 1) != 0)
        {
            list.Add((int)VirtualKey.SHIFT);
        }

        if ((flags & 4) != 0)
        {
            list.Add((int)VirtualKey.MENU);
        }

        return list.ToArray();
    }

    private static string KeyName(byte key, byte flags)
    {
        var prefix = ((flags & 2) != 0 ? "Ctrl+" : "") + ((flags & 1) != 0 ? "Shift+" : "") + ((flags & 4) != 0 ? "Alt+" : "");
        return prefix + (VirtualKey)key;
    }

    private static string Describe(in WalkRequest r)
    {
        var name = Svc.Data.GetExcelSheet<BNpcName>().GetRowOrDefault(r.NameId)?.Singular.ExtractText() ?? "?";
        return $"{name}（NameId {r.NameId}、Lv{(r.Level > 0 ? r.Level.ToString() : "?")}）聽覺範圍 {r.Radius:f1} 米、距離 {r.Distance:f1} 米";
    }

    // 只給 log 用：這一拍是不是騎乘中（騎著切成走路，不下坐騎）。
    private static string RideText()
    {
        return Svc.Condition[ConditionFlag.Mounted] ? "（騎乘中）" : string.Empty;
    }

    private static string ReasonText(WalkStopReason reason)
    {
        return reason switch
        {
            WalkStopReason.Passed => "前方與腳下都沒有聽覺範圍了",
            WalkStopReason.NotTravelling => "BOCCHI 沒在帶你移動：走到了、停止或換區",
            WalkStopReason.NotMoving => "vnavmesh 沒在走這一趟的路：你接手、讓位或重新尋路",
            WalkStopReason.Mounted => "騎乘中",
            WalkStopReason.Combat => "戰鬥中：已經被發現，照跑",
            WalkStopReason.Disabled => "設定關閉",
            WalkStopReason.Unavailable => "走路穿越暫時不能用",
            WalkStopReason.Paused => "BOCCHI 暫停更新：過場、讀取、對話或離開新月島",
            _ => "?",
        };
    }

    private static void Log(string message)
    {
        Svc.Log.Information($"[BOCCHI][仇恨迴避] 走路穿越：{message}");
    }
}

// 在 unsafe 類別裡不能 await：送鍵獨立成一個類別。
internal static class WalkKeyPress
{
    public static Task Run(byte key, int[]? modifiers, int holdMs)
    {
        return Task.Run(async () =>
        {
            WindowsKeypress.SendKeyHold(key, modifiers);
            try
            {
                await Task.Delay(holdMs).ConfigureAwait(false);
            }
            finally
            {
                WindowsKeypress.SendKeyRelease(key, modifiers);
            }
        });
    }
}
