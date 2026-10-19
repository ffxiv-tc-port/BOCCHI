using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BOCCHI.Automation;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Ocelot;
using Ocelot.Config.Attributes;
using Ocelot.Config.Handlers;
using Ocelot.Modules;

namespace BOCCHI.Modules.Automator;

/// <summary>依輔助職業分組列出 WrathCombo 的技能；勾選＝總開關打開時，非法模式或自動打怪執行期間可以使用。</summary>
public sealed class PhantomActionAllowListHandler : Handler
{
    private const string Key = "modules.automator.config.allowed_wrath_occult_options.";

    private readonly ModuleConfig owner;

    public PhantomActionAllowListHandler(ModuleConfig self, ConfigAttribute attribute, PropertyInfo prop)
        : base(self, attribute, prop)
    {
        owner = self;
    }

    protected override Type type
    {
        get => typeof(HashSet<string>);
    }

    protected override (bool handled, bool changed) RenderComponent(RenderContext context)
    {
        var changed = false;
        if (context.GetValue() is not HashSet<string> allowed)
        {
            allowed = new HashSet<string>(StringComparer.Ordinal);
            context.SetValue(allowed);
            changed = true;
        }

        ImGui.TextUnformatted(context.GetLabel());
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(I18N.T(Key + "tooltip"));
        }

        var allowList = (owner.Owner as Module)?.Plugin.Automation?.PhantomActions;
        if (allowList?.IsApplied == true)
        {
            ImGui.TextDisabled(I18N.T(Key + "status_active", new Dictionary<string, string>
            {
                ["allowed"] = allowList.AllowedCount.ToString(),
                ["blocked"] = allowList.BlockedCount.ToString(),
            }));
        }
        else
        {
            ImGui.TextDisabled(I18N.T(Key + (allowList?.CombatRunning == true ? "status_failed" : "status_idle")));
        }

        var groups = allowList?.Catalog.GetGroups();
        if (allowList == null || groups is not { Count: > 0 })
        {
            ImGui.TextDisabled(I18N.T(Key + "empty"));
            return (true, changed);
        }

        var sync = allowList.OptionSync;
        var syncOn = sync.State != PhantomOptionSync.Status.Off;
        if (syncOn)
        {
            DrawSyncStatus(sync, allowed, groups);
        }

        var edited = false;
        if (ImGui.SmallButton(I18N.T(Key + "select_all") + "##wrath_phantom_all"))
        {
            edited |= SetAll(allowed, groups, true);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton(I18N.T(Key + "select_none") + "##wrath_phantom_none"))
        {
            edited |= SetAll(allowed, groups, false);
        }

        if (edited && syncOn)
        {
            sync.RequestPushAll(groups);
        }

        // 這一幀送出的要求下一幀才處理：這一幀先不標，免得閃一下。
        var showMarks = sync.State == PhantomOptionSync.Status.Ready && !sync.HasPending;

        // 設定視窗開著時每幀都跑：走索引，不經介面的列舉器配置。
        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            var count = 0;
            var differs = 0;
            for (var i = 0; i < group.Options.Count; i++)
            {
                var on = allowed.Contains(group.Options[i].Name);
                count += on ? 1 : 0;
                differs += showMarks && sync.WrathValue(group.Options[i].Name) is { } wrath && wrath != on ? 1 : 0;
            }

            var differsNote = differs > 0 ? I18N.T(Key + "group_mismatch", new Dictionary<string, string> { ["count"] = differs.ToString() }) : string.Empty;
            if (!ImGui.TreeNode($"{group.JobName}（{count}/{group.Options.Count}）{differsNote}{group.HeaderId}"))
            {
                continue;
            }

            for (var i = 0; i < group.Options.Count; i++)
            {
                var option = group.Options[i];
                var isAllowed = allowed.Contains(option.Name);
                var wasAllowed = isAllowed;
                if (ImGui.Checkbox(option.CheckboxId, ref isAllowed))
                {
                    edited |= isAllowed ? allowed.Add(option.Name) : allowed.Remove(option.Name);
                    if (syncOn)
                    {
                        sync.RequestPush(option.Name);
                    }
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(option.Name);
                }

                if (showMarks)
                {
                    DrawMark(sync.WrathValue(option.Name), wasAllowed);
                }
            }

            ImGui.TreePop();
        }

        edited |= DrawStale(allowed, allowList.Catalog);

        if (edited)
        {
            context.SetValue(allowed);
            allowList.NotifyChanged();
        }

        return (true, changed || edited);
    }

    // 同步開著時清單上方的一行：讀不到、一致、或有幾項不同＋由使用者選哪一邊為準的兩顆按鈕。
    private static void DrawSyncStatus(PhantomOptionSync sync, HashSet<string> allowed, IReadOnlyList<WrathPhantomCatalog.Group> groups)
    {
        if (sync.State != PhantomOptionSync.Status.Ready)
        {
            var unavailable = sync.State == PhantomOptionSync.Status.Unavailable;
            ImGui.TextColored(unavailable ? ImGuiColors.DalamudYellow : ImGuiColors.DalamudGrey, I18N.T(Key + (unavailable ? "sync_unavailable" : "sync_reading")));
            return;
        }

        var differs = 0;
        for (var g = 0; g < groups.Count; g++)
        {
            var options = groups[g].Options;
            for (var i = 0; i < options.Count; i++)
            {
                differs += sync.WrathValue(options[i].Name) is { } wrath && wrath != allowed.Contains(options[i].Name) ? 1 : 0;
            }
        }

        if (differs == 0)
        {
            ImGui.TextDisabled(I18N.T(Key + "sync_ok"));
            return;
        }

        ImGui.TextColored(ImGuiColors.DalamudYellow, I18N.T(Key + "sync_mismatch", new Dictionary<string, string> { ["count"] = differs.ToString() }));
        if (ImGui.SmallButton(I18N.T(Key + "sync_use_list") + "##wrath_phantom_sync_list"))
        {
            sync.RequestPushAll(groups);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(I18N.T(Key + "sync_use_list_tooltip"));
        }

        ImGui.SameLine();
        if (ImGui.SmallButton(I18N.T(Key + "sync_use_wrath") + "##wrath_phantom_sync_wrath"))
        {
            sync.RequestImport();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(I18N.T(Key + "sync_use_wrath_tooltip"));
        }
    }

    // 與 WrathCombo 自己的設定不同的項目標在列上；不知道也標出來。
    private static void DrawMark(bool? wrath, bool listed)
    {
        if (wrath == listed)
        {
            return;
        }

        ImGui.SameLine();
        if (wrath is not { } on)
        {
            ImGui.TextDisabled(I18N.T(Key + "mark_unknown"));
            return;
        }

        ImGui.TextColored(ImGuiColors.DalamudYellow, I18N.T(Key + (on ? "mark_on" : "mark_off")));
    }

    private static bool SetAll(HashSet<string> allowed, IReadOnlyList<WrathPhantomCatalog.Group> groups, bool on)
    {
        var edited = false;
        for (var g = 0; g < groups.Count; g++)
        {
            var options = groups[g].Options;
            for (var i = 0; i < options.Count; i++)
            {
                edited |= on ? allowed.Add(options[i].Name) : allowed.Remove(options[i].Name);
            }
        }

        return edited;
    }

    // 目前的 WrathCombo 沒有的項目（改名或移除）套用時會略過，列出來讓使用者清掉。
    private static bool DrawStale(HashSet<string> allowed, WrathPhantomCatalog catalog)
    {
        var stale = 0;
        foreach (var name in allowed)
        {
            if (!catalog.Contains(name))
            {
                stale++;
            }
        }

        if (stale == 0)
        {
            return false;
        }

        ImGui.TextDisabled(I18N.T(Key + "stale", new Dictionary<string, string> { ["count"] = stale.ToString() }));
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(string.Join("\n", allowed.Where(name => !catalog.Contains(name))));
        }

        ImGui.SameLine();
        if (!ImGui.SmallButton(I18N.T(Key + "stale_clear") + "##wrath_phantom_stale"))
        {
            return false;
        }

        return allowed.RemoveWhere(name => !catalog.Contains(name)) > 0;
    }
}
