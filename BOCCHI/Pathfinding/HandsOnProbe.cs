using System;
using System.Collections.Generic;
using BOCCHI.Automation;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BOCCHI.Pathfinding;

/// <summary>
/// 「使用者剛按過移動鍵」的記憶：按著時每拍記下，放開後 <see cref="LatchMs"/> 內仍算剛按過。
/// 純邏輯、不碰遊戲（離線驗收直接驅動）；只在 framework 執行緒用。時間一律用呼叫端給的 now，另用旗標記「按過沒」，不拿 0 當「沒有」。
/// </summary>
public sealed class HandsOnLatch
{
    /// <summary>放開後還算剛按過的時間：涵蓋讓位取樣的間隔與速度時間窗的落後（短按的角色停下後約 0.4 秒速度才歸零）。</summary>
    public const long LatchMs = 1_000;

    private bool seen;

    private long lastAt;

    public void Note(bool pressed, long now)
    {
        if (pressed)
        {
            seen = true;
            lastAt = now;
        }
    }

    public bool Active(long now)
    {
        return seen && now - lastAt <= LatchMs;
    }
}

/// <summary>
/// 全插件共用的「使用者最近在操作」：讀到移動鍵、或任何一處讓位偵測正在讓位時記下時刻，返回施放前來問。
/// 純邏輯、不碰遊戲；只在 framework 執行緒讀寫。時間用呼叫端給的 now，另用旗標記「記過沒」。
/// </summary>
public static class HandsOnRecent
{
    /// <summary>這麼久內記過就算「最近在操作」。</summary>
    public const long WindowMs = 1_500;

    private static bool seen;

    private static long lastAt;

    private static string lastWhat = string.Empty;

    public static void Note(long now, string what)
    {
        seen = true;
        lastAt = now;
        lastWhat = what;
    }

    /// <param name="what">最近一次記下的是什麼（給記錄用）；沒記過是空字串。</param>
    public static bool Within(long now, out string what)
    {
        what = lastWhat;
        return seen && now - lastAt <= WindowMs;
    }

    public static void Clear()
    {
        seen = false;
        lastWhat = string.Empty;
    }
}

/// <summary>
/// 讓位用的「使用者在操作」訊號：鍵盤移動鍵（WASD、QE、方向鍵）、滑鼠左右鍵同按、手把左搖桿，以及不是 BOCCHI 自己發起的互動事件。
/// 打字中（遊戲或插件視窗的文字框；判斷同 Aggro.WalkThrough）鍵盤不算，滑鼠與手把照算。
/// 只在 framework 執行緒用。讀不到（服務不在、擲例外）就當成沒按，之後不再讀＝與沒有這個判斷時相同。
/// 自訂按鍵綁定讀不到：那時只剩原本「持續移動才讓位」的判斷。
/// </summary>
public sealed class HandsOnProbe
{
    /// <summary>手把左搖桿的值域約 ±99，超過這個值才算在推。</summary>
    public const int StickThreshold = 30;

    /// <summary>BOCCHI 自己送出互動之後這麼久內的互動事件不算使用者在操作（開箱事件約一兩秒）。</summary>
    public const long OwnInteractionMs = 10_000;

    private static readonly VirtualKey[] Candidates =
    [
        VirtualKey.W, VirtualKey.A, VirtualKey.S, VirtualKey.D, VirtualKey.Q, VirtualKey.E,
        VirtualKey.UP, VirtualKey.DOWN, VirtualKey.LEFT, VirtualKey.RIGHT,
    ];

    private readonly HandsOnLatch latch = new();

    private VirtualKey[]? keys;

    private bool mouseValid;

    private bool broken;

    /// <summary>每拍呼叫（不要只在取樣那一拍）：記下這一拍有沒有按移動鍵，回傳剛按過沒。</summary>
    public bool ReadInput(long now)
    {
        if (!broken)
        {
            try
            {
                var pressed = Pressed();
                latch.Note(pressed, now);
                if (pressed)
                {
                    HandsOnRecent.Note(now, "按了移動鍵");
                }
            }
            catch (Exception ex)
            {
                broken = true;
                Svc.Log.Information($"[BOCCHI][趕路讓位] 讀不到按鍵／手把狀態（{ex.GetType().Name}：{ex.Message}）：之後只看角色有沒有持續移動。");
            }
        }

        return latch.Active(now);
    }

    /// <summary>在開箱、對話一類的互動事件裡，而且不是 BOCCHI 自己剛送出的互動。</summary>
    public static bool Interacting(long now)
    {
        var condition = Svc.Condition;
        return IsUserInteraction(condition[ConditionFlag.OccupiedInEvent] || condition[ConditionFlag.OccupiedInQuestEvent], now);
    }

    /// <summary>互動事件裡、而且不是 BOCCHI 自己剛送出的互動（純邏輯，離線驗收直接呼叫）。</summary>
    public static bool IsUserInteraction(bool occupied, long now)
    {
        return occupied && !OwnInteraction.Recent(now, OwnInteractionMs);
    }

    /// <param name="gameTyping">遊戲的文字輸入框在用；null＝解不出遊戲函式，當成打字中。</param>
    public static bool IgnoreKeyboard(bool imguiTyping, bool? gameTyping)
    {
        return imguiTyping || gameTyping != false;
    }

    private static unsafe bool? GameTextInputActive()
    {
        if (AtkModule.MemberFunctionPointers.IsTextInputActive == null)
        {
            return null;
        }

        var module = RaptureAtkModule.Instance();
        return module == null ? null : module->AtkModule.IsTextInputActive();
    }

    private bool Pressed()
    {
        var state = Svc.KeyState;
        if (keys == null)
        {
            var valid = new List<VirtualKey>(Candidates.Length);
            foreach (var key in Candidates)
            {
                if (state.IsVirtualKeyValid(key))
                {
                    valid.Add(key);
                }
            }

            keys = valid.ToArray();
            mouseValid = state.IsVirtualKeyValid(VirtualKey.LBUTTON) && state.IsVirtualKeyValid(VirtualKey.RBUTTON);
        }

        if (keys.Length > 0 && !IgnoreKeyboard(ImGui.GetIO().WantTextInput, GameTextInputActive()))
        {
            foreach (var key in keys)
            {
                if (state[key])
                {
                    return true;
                }
            }
        }

        if (mouseValid && state[VirtualKey.LBUTTON] && state[VirtualKey.RBUTTON])
        {
            return true;
        }

        var stick = Svc.GamepadState.LeftStick;
        return Math.Abs(stick.X) >= StickThreshold || Math.Abs(stick.Y) >= StickThreshold;
    }
}
