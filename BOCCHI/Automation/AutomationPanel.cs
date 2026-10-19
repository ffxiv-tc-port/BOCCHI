using System;
using System.Collections.Generic;
using System.Numerics;
using BOCCHI.Pathfinding;
using BOCCHI.Windows;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using Ocelot;

namespace BOCCHI.Automation;

/// <summary>
/// 主視窗最上方的自動化控制：每一項的狀態＋開始／停止（寬度夠時排兩欄），以及「全部停止」。
/// 寶箱／幸運胡蘿蔔巡迴的第一顆鈕跟著狀態換：跑的時候是暫停、暫停中是續跑、沒有可續跑的進度時是開始（從頭）；
/// 停止鈕在暫停中改成放棄進度。按鈕數與其他項目相同，兩欄排得下。
/// 非法模式的目前活動也畫在這裡，顯示條件與 ModuleManager 篩選要畫哪些模組的條件相同（非法模式開著才畫）。
/// </summary>
public class AutomationPanel
{
    private static readonly Vector4 RunningColor = new(0.42f, 0.85f, 0.45f, 1f);

    private static readonly Vector4 StoppedColor = new(0.62f, 0.62f, 0.62f, 1f);

    private static readonly Vector4 PausedColor = new(0.95f, 0.75f, 0.3f, 1f);

    // 按鈕 ID 載入時組好一次：每幀組字串會配置。
    private static readonly string[] StartIds = BuildIds("automation-start-");

    private static readonly string[] StopIds = BuildIds("automation-stop-");

    private static readonly string[] PauseIds = BuildIds("automation-pause-");

    private static readonly string[] ResumeIds = BuildIds("automation-resume-");

    // 圖示字元先轉好：IconButton(FontAwesomeIcon) 每次呼叫都會新建一個字串。
    private static readonly string SkipIcon = FontAwesomeIcon.FastForward.ToIconString();

    private static readonly string ClearIcon = FontAwesomeIcon.Eraser.ToIconString();

    private static readonly string StopIcon = FontAwesomeIcon.Stop.ToIconString();

    private static readonly string PlayIcon = FontAwesomeIcon.Play.ToIconString();

    private static readonly string PauseIcon = FontAwesomeIcon.Pause.ToIconString();

    private static readonly string ResumeIcon = FontAwesomeIcon.StepForward.ToIconString();

    private static readonly string RestoreIcon = FontAwesomeIcon.Redo.ToIconString();

    // 上一幀量到最寬的一格；0＝還沒量過（先排一欄）。
    private float cellWidth;

    public void Draw(AutomationControl control)
    {
        MainPanelUi.Title(I18N.T("automation.panel.title"), true);
        ImGui.SameLine();
        if (ImGuiEx.IconButton(StopIcon, "automation-stop-all"))
        {
            control.StopAll(I18N.T("automation.source.main_window"), false);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(I18N.T("automation.panel.stop_all_tooltip"));
        }

        ImGui.SameLine();
        DrawComboButton(control);

        ImGui.Indent(16);
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var columns = cellWidth > 0 && ImGui.GetContentRegionAvail().X >= (cellWidth * 2) + (spacing * 4) ? 2 : 1;
        if (ImGui.BeginTable("##bocchi-automation", columns, ImGuiTableFlags.SizingFixedFit))
        {
            var widest = 0f;
            var kinds = AutomationKindExtensions.All;
            for (var i = 0; i < kinds.Length; i++)
            {
                ImGui.TableNextColumn();
                widest = Math.Max(widest, DrawCell(control, kinds[i], i));
            }

            ImGui.EndTable();
            cellWidth = widest;
        }

        DrawDeathRestore(control);
        DrawIllegalModeActivity(control);
        DrawSkipped(control);
        ImGui.Unindent(16);
    }

    // 組合全部在跑時是「停止組合」（只停組合裡的），否則是「開始組合」；開不起來時淡化，說明框寫原因。
    private static void DrawComboButton(AutomationControl control)
    {
        var status = control.GetComboStatus();
        var running = status == ComboStatus.Running;
        var enabled = status is ComboStatus.Ready or ComboStatus.Running;
        ImGui.PushID("automation-combo");
        var pressed = ImGuiEx.Button(I18N.T(running ? "automation.panel.combo_stop" : "automation.panel.combo_start"), enabled);
        ImGui.PopID();
        if (pressed)
        {
            control.ToggleCombo(I18N.T("automation.source.main_window"), false);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            var names = new Dictionary<string, string> { ["names"] = control.ComboNames() };
            var help = I18N.T(running ? "automation.panel.combo_stop_tooltip" : "automation.panel.combo_start_tooltip", names);
            ImGui.SetTooltip(enabled ? help : control.ComboWhyNot(status) + "\n\n" + help);
        }
    }

    // 只畫在非法模式那一格：略過目前選定的那一場、非法模式不停。
    private static void DrawSkipButton(AutomationControl control)
    {
        var canSkip = control.CanSkipCurrent(out var whyNotKey);
        if (ImGuiEx.IconButton(SkipIcon, "automation-skip-illegal", enabled: canSkip))
        {
            control.SkipCurrent(I18N.T("automation.source.main_window"), false);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            var help = I18N.T("automation.skip.tooltip");
            ImGui.SetTooltip(canSkip ? help : I18N.T(whyNotKey) + "\n\n" + help);
        }
    }

    // 有略過的場次才畫（非法模式停著也畫：停了又開時一樣生效）；說明框列名稱，旁邊的鈕清空。
    private static void DrawSkipped(AutomationControl control)
    {
        var count = control.SkipCount;
        if (count == 0)
        {
            return;
        }

        MainPanelUi.Labelled(I18N.T("automation.skip.label"), $"{count}");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(I18N.T("automation.skip.list_tooltip", new Dictionary<string, string> { ["names"] = control.SkippedNames() }));
        }

        ImGui.SameLine();
        if (ImGuiEx.IconButton(ClearIcon, "automation-skip-clear"))
        {
            control.ClearSkips(I18N.T("automation.source.main_window"), false);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(I18N.T("automation.skip.clear_tooltip"));
        }
    }

    /// <returns>這一格畫出來的寬度。</returns>
    private static float DrawCell(AutomationControl control, AutomationKind kind, int index)
    {
        var start = ImGui.GetCursorScreenPos().X;
        var running = control.IsRunning(kind);
        var canStart = control.CanStart(kind, out var whyNot);
        var hunter = control.GetHunter(kind);
        var saved = hunter is { Progress.HasSaved: true };

        // 啟用與否用淡化表示（不用 BeginDisabled），說明框在停用時照樣出得來。
        if (hunter == null || !DrawHuntFirstButton(control, kind, index, hunter, running, canStart, whyNot))
        {
            if (ImGuiEx.IconButton(PlayIcon, StartIds[index], enabled: canStart))
            {
                control.Start(kind, I18N.T("automation.source.main_window"), false);
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(!canStart ? whyNot
                    : saved ? StartFreshTooltip(hunter!)
                    : I18N.T("generic.label.start"));
            }
        }

        ImGui.SameLine();
        if (ImGuiEx.IconButton(StopIcon, StopIds[index], enabled: running || saved))
        {
            if (running)
            {
                control.Stop(kind, I18N.T("automation.source.main_window"), false);
            }
            else
            {
                control.AbandonProgress(kind, I18N.T("automation.source.main_window"), false);
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(hunter == null ? I18N.T("generic.label.stop")
                : running ? I18N.T("automation.panel.stop_discard_tooltip")
                : saved ? I18N.T("automation.panel.abandon_tooltip", new Dictionary<string, string> { ["progress"] = hunter.ProgressText })
                : I18N.T("generic.label.stop"));
        }

        if (kind == AutomationKind.IllegalMode)
        {
            ImGui.SameLine();
            DrawSkipButton(control);
        }

        ImGui.SameLine();
        if (running)
        {
            ImGui.TextColored(RunningColor, I18N.T("automation.panel.running"));
        }
        else if (saved)
        {
            // 列上只放狀態（點數放列上會擠成一欄）；已跑幾點與失效原因在說明框。
            var valid = hunter!.Progress.State == HuntProgressState.Paused;
            ImGui.TextColored(valid ? PausedColor : StoppedColor, I18N.T(valid ? "automation.panel.paused" : "automation.panel.progress_invalid"));
            if (ImGui.IsItemHovered())
            {
                var progress = I18N.T("automation.panel.progress_tooltip", new Dictionary<string, string> { ["progress"] = hunter.ProgressText });
                ImGui.SetTooltip(hunter.CanResume(out var why) ? progress : AutomationControl.ResumeBlockedText(hunter, why) + "\n" + progress);
            }
        }
        else
        {
            ImGui.TextColored(StoppedColor, I18N.T("automation.panel.stopped"));
        }

        ImGui.SameLine();

        // 列上放短名；完整名稱（含說明）在 tooltip。
        var shortName = I18N.T(kind.ShortNameKey());
        ImGui.TextUnformatted(shortName);
        if (ImGui.IsItemHovered())
        {
            var fullName = AutomationControl.GetName(kind);
            if (!string.Equals(fullName, shortName, StringComparison.Ordinal))
            {
                ImGui.SetTooltip(fullName);
            }
        }

        return ImGui.GetItemRectMax().X - start;
    }

    /// <returns>false＝這一格的第一顆鈕照一般的「開始」畫（沒在跑、也沒有可續跑的進度；失效的進度改成從頭開始）。</returns>
    private static bool DrawHuntFirstButton(AutomationControl control, AutomationKind kind, int index, Hunter hunter, bool running, bool canStart, string whyNot)
    {
        if (running)
        {
            if (ImGuiEx.IconButton(PauseIcon, PauseIds[index]))
            {
                control.Pause(kind, I18N.T("automation.source.main_window"), false);
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(I18N.T("automation.panel.pause_tooltip"));
            }

            return true;
        }

        if (!hunter.CanResume(out _))
        {
            return false;
        }

        // 能續跑但被別的自動化擋住時停用，原因與開始相同（例：請先停止非法模式）。
        if (ImGuiEx.IconButton(ResumeIcon, ResumeIds[index], enabled: canStart))
        {
            control.Resume(kind, I18N.T("automation.source.main_window"), false);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(canStart ? I18N.T("automation.panel.resume_tooltip", new Dictionary<string, string> { ["progress"] = hunter.ProgressText }) : whyNot);
        }

        return true;
    }

    // 進度失效時說明為什麼不能續跑，再說按下去會從頭開始。
    private static string StartFreshTooltip(Hunter hunter)
    {
        var fresh = I18N.T("automation.panel.start_fresh_tooltip", new Dictionary<string, string> { ["progress"] = hunter.ProgressText });
        return hunter.CanResume(out var why) ? fresh : AutomationControl.ResumeBlockedText(hunter, why) + "\n" + fresh;
    }

    // 「死亡後提示恢復自動化」開著、而且有倒地時停下的清單才畫；按下才恢復。
    private static void DrawDeathRestore(AutomationControl control)
    {
        if (!control.HasDeathRestoreOffer)
        {
            return;
        }

        var dead = Svc.Objects.LocalPlayer?.IsDead == true;
        if (ImGuiEx.IconButton(RestoreIcon, "automation-death-restore", enabled: !dead))
        {
            control.RestoreAfterDeath(I18N.T("automation.source.main_window"), false);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(dead ? I18N.T("automation.blocked.dead") : I18N.T("automation.panel.death_restore_tooltip"));
        }

        ImGui.SameLine();
        ImGui.TextColored(PausedColor, control.DeathRestoreLabel);
    }

    private static void DrawIllegalModeActivity(AutomationControl control)
    {
        // 與 ModuleManager 篩選要畫哪些模組的條件相同：非法模式開著才畫。
        if (control.Automator is not { ShouldRender: true, HasRequiredIPCs: true } automator)
        {
            return;
        }

        automator.panel.DrawCompact(automator);
    }

    private static string[] BuildIds(string prefix)
    {
        var kinds = AutomationKindExtensions.All;
        var ids = new string[kinds.Length];
        for (var i = 0; i < kinds.Length; i++)
        {
            ids[i] = prefix + kinds[i].CommandKey();
        }

        return ids;
    }
}
