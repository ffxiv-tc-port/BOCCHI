using System;
using System.Collections.Generic;
using BOCCHI.Data;
using BOCCHI.Modules.Teleporter;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Ocelot;
using AddonSheet = Lumina.Excel.Sheets.Addon;

namespace BOCCHI.Modules.CriticalEncounters;

// 本檔契約：跟團刷 CE 的狀態只在框架執行緒讀寫（自己的 Framework.Update、換區／登出事件、AddonLifecycle、主視窗）。
// 武裝只在記憶體：重載插件、登出就解除；沒武裝時每幀第一行就返回，行為與沒有這個功能時相同。
// 不變式：進入 Waiting 只有兩條路（在島上按武裝、Ready 時按出發），進島一律停在 Ready ⇒ 移動與退出副本都要使用者在這次進島按過。
// 退出副本只走介面（副本選單「退出任務」→ 確認框），不呼叫任何原生的放棄任務函式。
// 確認框照 TeleporterModule 的規則：在 PreSetup 比中句子才請 YesAlready 暫停並記下那一扇，延到下一幀只按同一位址、同一句；暫停失敗就不按。
// 視窗位址只做等值比較、永不解參；每次要碰視窗都當幀重新以名稱取。

/// <summary>
/// 跟團刷 CE：主視窗武裝一次之後，每次進新月島停在待命、按「出發」才等目標緊急遭遇戰開放報名，用主視窗「跑過去」同一條尋路動作鏈過去，
/// 戰鬥照舊交給 BMR／WrathCombo，那一場成功結束後退出副本。下一輪由團長排、YesAlready 接受，這裡不碰任務搜尋器。
/// </summary>
public sealed class CeFarm : IDisposable
{
    private const string Tag = "[BOCCHI][跟團刷CE]";

    private const string YesnoAddon = SelectYesnoSetup.AddonName;

    private const string MenuAddon = "ContentsFinderMenu";

    /// <summary>Addon#2545＝「確定要退出任務嗎？」。</summary>
    private const uint LeavePromptRow = 2545;

    /// <summary>Addon#3792＝副本選單的「退出任務」。</summary>
    private const uint LeaveEntryRow = 3792;

    private const int YesAlreadyPauseMs = 3_000;

    private const int PressDeadlineMs = 2_000;

    private const long MenuRetryMs = 1_000;

    // 按了「退出任務」之後這麼久都沒有確認框，才回頭關選單重來；關閉中的選單只有幾幀，遠小於這個值。
    private const long MenuSettleMs = 3_000;

    // 關掉選單之後這麼久內不再碰它（關閉中的視窗仍取得到、也判得成就緒）。
    private const long MenuCooldownMs = 1_000;

    private const int MaxMenuPresses = 3;

    private const long PartyPollMs = 1_000;

    // 成功之後一直不能退出（戰鬥中、詠唱中）的上限。
    private const long ExitDelayLimitMs = 5 * 60_000;

    private readonly CriticalEncountersModule module;

    private CeFarmPhase phase;

    private uint target;

    private string targetName = string.Empty;

    private int roundLimit;

    private int roundsDone;

    private long phaseSince;

    private long waitStart;

    private long registerSeenAt;

    private bool travelSent;

    private bool travelSkipReported;

    private CriticalEncounterHistory.Entry? entry;

    private long exitAt;

    private long partyAbsentMs;

    private long lastPartyPollAt;

    private long nextMenuShowAt;

    private long nextPressAt;

    private nint pressedMenu;

    private long pressedAt;

    private int menuPresses;

    private int menuShows;

    // 選單可見但一直沒就緒的起點；0＝沒在等。
    private long notReadySince;

    private bool confirmSeen;

    private readonly YesnoClaim claim = new();

    private string? leavePrompt;

    private bool leavePromptLoaded;

    private string? leaveEntry;

    private bool leaveEntryLoaded;

    private static bool menuDumped;

    public CeFarm(CriticalEncountersModule module)
    {
        this.module = module;
        Svc.Framework.Update += OnUpdate;
        Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
        Svc.ClientState.Logout += OnLogout;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreSetup, YesnoAddon, OnYesnoPreSetup);
    }

    public CeFarmPhase Phase
    {
        get => phase;
    }

    public bool Armed
    {
        get => phase != CeFarmPhase.Off;
    }

    public string TargetName
    {
        get => targetName;
    }

    public int RoundLimit
    {
        get => roundLimit;
    }

    public int RoundsDone
    {
        get => roundsDone;
    }

    /// <summary>在島上等目標開放報名已經等了多久（毫秒）；不是在等時是 -1。</summary>
    public long WaitedMs
    {
        get => phase == CeFarmPhase.Waiting ? Environment.TickCount64 - waitStart : -1;
    }

    /// <summary>主視窗「武裝」：成功回 true；不能武裝時聊天說明原因。</summary>
    public bool Arm(string source)
    {
        if (Armed)
        {
            return false;
        }

        var config = module.Config;
        var block = CeFarmRules.CanArm(config.CeFarmTarget, module.IsEnabled, InParty(), module.Plugin.Automation?.IsAnyMovementRunning() == true);
        if (block != CeFarmArmBlock.None)
        {
            var why = I18N.T(BlockKey(block));
            Svc.Log.Information($"{Tag} 不能武裝（{source}）：{why}");
            Svc.Chat.Print(I18N.T("modules.critical_encounters.farm.messages.cannot_arm", new Dictionary<string, string> { ["reason"] = why }));
            return false;
        }

        var now = Environment.TickCount64;
        target = config.CeFarmTarget;
        targetName = CeName(target);
        roundLimit = Math.Max(0, config.CeFarmRounds);
        roundsDone = 0;
        partyAbsentMs = 0;
        lastPartyPollAt = now;
        if (ZoneData.IsInOccultCrescent())
        {
            EnterWaiting(now);
        }
        else
        {
            SetPhase(CeFarmPhase.Away, now);
        }

        var rounds = RoundsText(roundLimit);
        Svc.Log.Information($"{Tag} 武裝（{source}）：目標「{targetName}」({target})，{rounds}，開始等之後最多等 {config.CeFarmWaitMinutes} 分鐘開放報名；目前{(phase == CeFarmPhase.Waiting ? "在島上，這一輪直接開始等" : "不在島上，等團長排進島（進島後要按出發）")}");
        Svc.Chat.Print(I18N.T("modules.critical_encounters.farm.messages.armed", new Dictionary<string, string> { ["name"] = targetName, ["rounds"] = rounds }));
        return true;
    }

    /// <summary>主視窗「出發」：只在 <see cref="CeFarmPhase.Ready"/> 有作用，開始這一輪的等待。</summary>
    public bool Start(string source)
    {
        if (phase != CeFarmPhase.Ready)
        {
            return false;
        }

        EnterWaiting(Environment.TickCount64);
        Svc.Log.Information($"{Tag} 出發（{source}）：第 {roundsDone + 1} 輪，等「{targetName}」開放報名（最多 {module.Config.CeFarmWaitMinutes} 分鐘）");
        return true;
    }

    /// <summary>解除武裝並在聊天說明一行；沒武裝時什麼都不做。<paramref name="detail"/> 給 StopAll 的原因或補充說明。</summary>
    public void Disarm(CeFarmStop stop, string? detail = null)
    {
        if (!Armed)
        {
            return;
        }

        var reason = ReasonText(stop, detail);
        Svc.Log.Information($"{Tag} 解除：{reason}（完成 {roundsDone} 輪，{RoundsText(roundLimit)}，目標「{targetName}」，停在 {phase}）");
        Svc.Chat.Print(I18N.T("modules.critical_encounters.farm.messages.stopped", new Dictionary<string, string> { ["reason"] = reason, ["done"] = roundsDone.ToString() }));
        Reset();
    }

    public void Dispose()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Svc.ClientState.Logout -= OnLogout;
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreSetup, YesnoAddon, OnYesnoPreSetup);
    }

    private void Reset()
    {
        phase = CeFarmPhase.Off;
        entry = null;
        travelSent = false;
        travelSkipReported = false;
        registerSeenAt = 0;
        ResetExitUi();
    }

    private void ResetExitUi()
    {
        nextMenuShowAt = 0;
        nextPressAt = 0;
        pressedMenu = 0;
        pressedAt = 0;
        menuPresses = 0;
        menuShows = 0;
        notReadySince = 0;
        confirmSeen = false;
        claim.Clear();
    }

    private void SetPhase(CeFarmPhase next, long now)
    {
        phase = next;
        phaseSince = now;
    }

    private void EnterWaiting(long now)
    {
        SetPhase(CeFarmPhase.Waiting, now);
        waitStart = now;
        registerSeenAt = 0;
        travelSent = false;
        travelSkipReported = false;
        entry = null;
        ResetExitUi();
    }

    private void EnterReady(long now)
    {
        SetPhase(CeFarmPhase.Ready, now);
        registerSeenAt = 0;
        travelSent = false;
        travelSkipReported = false;
        entry = null;
        ResetExitUi();
    }

    private void OnUpdate(IFramework framework)
    {
        if (phase == CeFarmPhase.Off)
        {
            return;
        }

        try
        {
            Tick(Environment.TickCount64);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"{Tag} 每幀處理擲出例外");
            Disarm(CeFarmStop.Manual, ex.GetType().Name);
        }
    }

    private void Tick(long now)
    {
        if (!Svc.ClientState.IsLoggedIn)
        {
            return;
        }

        TickPendingPress(now);
        var player = Svc.Objects.LocalPlayer;
        if (player == null)
        {
            return;
        }

        if (player.IsDead)
        {
            Disarm(CeFarmStop.Dead);
            return;
        }

        if (!TickParty(now))
        {
            return;
        }

        if (phase is CeFarmPhase.Waiting or CeFarmPhase.Joined && !module.IsEnabled)
        {
            Disarm(CeFarmStop.ModuleDisabled);
            return;
        }

        switch (phase)
        {
            case CeFarmPhase.Waiting:
                TickWaiting(now);
                break;
            case CeFarmPhase.Joined:
                TickJoined(now);
                break;
            case CeFarmPhase.ExitDelay:
                TickExitDelay(now);
                break;
            case CeFarmPhase.Exiting:
                TickExiting(now);
                break;
        }
    }

    // 回 false＝已經解除。
    private bool TickParty(long now)
    {
        if (now - lastPartyPollAt < PartyPollMs)
        {
            return true;
        }

        // 讀圖時角色是 null、這裡不會跑：恢復後那一拍最多算兩拍，不把整段讀圖算成不在小隊。
        var elapsed = Math.Min(now - lastPartyPollAt, 2 * PartyPollMs);
        lastPartyPollAt = now;
        var condition = Svc.Condition;
        var betweenAreas = condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51];
        partyAbsentMs = CeFarmRules.NextAbsence(partyAbsentMs, InParty(), betweenAreas, elapsed);
        if (!CeFarmRules.LeftParty(partyAbsentMs))
        {
            return true;
        }

        Disarm(CeFarmStop.LeftParty);
        return false;
    }

    private void TickWaiting(long now)
    {
        if (!ZoneData.IsInOccultCrescent())
        {
            return;
        }

        var id = (ushort)target;
        if (module.History.TryGetOpen(id, out var open) && open.Participated)
        {
            entry = open;
            SetPhase(CeFarmPhase.Joined, now);
            Svc.Log.Information($"{Tag} 第 {roundsDone + 1} 輪：「{targetName}」已經在參加（{open.LastState}），等它結束");
            return;
        }

        // 逾時只在目標沒開著報名時判：已經開放報名就照常跑過去。
        if (!module.CriticalEncounters.TryGetValue(target, out var snapshot) || snapshot.State != DynamicEventState.Register)
        {
            registerSeenAt = 0;
            var minutes = module.Config.CeFarmWaitMinutes;
            if (CeFarmRules.WaitTimedOut(now - waitStart, minutes))
            {
                Disarm(CeFarmStop.WaitTimeout, minutes.ToString());
            }

            return;
        }

        if (registerSeenAt == 0)
        {
            registerSeenAt = now;
            Svc.Log.Information($"{Tag} 第 {roundsDone + 1} 輪：「{targetName}」開放報名（進島後 {(now - waitStart) / 1000f:F0} 秒），{CeFarmRules.TravelDelayMs / 1000} 秒後跑過去");
        }

        if (!CeFarmRules.ShouldTravel(snapshot.State, now - registerSeenAt, travelSent) || !IsFree())
        {
            return;
        }

        if (module.Plugin.Automation?.IsAnyMovementRunning() == true)
        {
            if (!travelSkipReported)
            {
                travelSkipReported = true;
                Svc.Log.Information($"{Tag} 第 {roundsDone + 1} 輪：有會自己移動的自動化在跑，不送「跑過去」（交給它），照樣等這一場結束");
            }

            module.History.TryGetOpen(id, out entry);
            SetPhase(CeFarmPhase.Joined, now);
            return;
        }

        if (!TryTravel(snapshot))
        {
            return;
        }

        travelSent = true;
        module.History.TryGetOpen(id, out entry);
        SetPhase(CeFarmPhase.Joined, now);
        var at = snapshot.Position;
        Svc.Log.Information($"{Tag} 第 {roundsDone + 1} 輪：跑過去「{targetName}」（{at.X:F1}, {at.Y:F1}, {at.Z:F1}），戰鬥交給 BMR／WrathCombo");
    }

    private bool TryTravel(CriticalEncounterSnapshot snapshot)
    {
        if (!EventData.CriticalEncounters.TryGetValue(target, out var data) || !module.TryGetModule<TeleporterModule>(out var teleporter) || teleporter == null)
        {
            return false;
        }

        return teleporter.teleporter.PathfindTo(snapshot.Position, snapshot.Name, data);
    }

    private void TickJoined(long now)
    {
        if (entry == null)
        {
            module.History.TryGetOpen((ushort)target, out entry);
        }

        if (entry?.EndUtc == null)
        {
            if (now - phaseSince > CeFarmRules.JoinedTimeLimitMs)
            {
                Disarm(CeFarmStop.JoinedTooLong, (CeFarmRules.JoinedTimeLimitMs / 60_000).ToString());
            }

            return;
        }

        var ended = entry;
        var stop = CeFarmRules.JudgeEnd(ended.Outcome, ended.Participated);
        Svc.Log.Information($"{Tag} 第 {roundsDone + 1} 輪：「{targetName}」結束，結果={ended.Outcome} 參加={(ended.Participated ? "是" : "否")}"
                            + $"{(ended.Unobserved ? "（結束前一刻沒在更新）" : string.Empty)}；{(stop == CeFarmStop.None ? $"{CeFarmRules.ExitDelayMs / 1000} 秒後退出副本" : "停止")}");
        if (stop != CeFarmStop.None)
        {
            Disarm(stop);
            return;
        }

        exitAt = now + CeFarmRules.ExitDelayMs;
        SetPhase(CeFarmPhase.ExitDelay, now);
    }

    private void TickExitDelay(long now)
    {
        if (!ZoneData.IsInOccultCrescent() || now < exitAt)
        {
            return;
        }

        if (!IsFree())
        {
            if (now - phaseSince > ExitDelayLimitMs)
            {
                Disarm(CeFarmStop.ExitTimeout, "busy");
            }

            return;
        }

        ResetExitUi();
        SetPhase(CeFarmPhase.Exiting, now);
        Svc.Log.Information($"{Tag} 第 {roundsDone + 1} 輪：退出副本（副本選單「退出任務」→ 確認框；{CeFarmRules.ExitTimeLimitMs / 1000} 秒內沒離開新月島就停止）");
    }

    private unsafe void TickExiting(long now)
    {
        if (now - phaseSince > CeFarmRules.ExitTimeLimitMs)
        {
            Svc.Log.Information($"{Tag} 退出逾時：叫出副本選單 {menuShows} 次、按「退出任務」{menuPresses} 次、退出確認框{(confirmSeen ? "看到了" : "沒看到")}");
            Disarm(CeFarmStop.ExitTimeout, confirmSeen ? "confirm" : null);
            return;
        }

        if (claim.IsActive || confirmSeen || !IsFree())
        {
            return;
        }

        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(YesnoAddon, out var yesno) && GenericHelpers.IsAddonReady(yesno))
        {
            return;
        }

        var found = GenericHelpers.TryGetAddonByName<AtkUnitBase>(MenuAddon, out var menu);
        var ready = found && GenericHelpers.IsAddonReady(menu);
        if (pressedMenu != 0)
        {
            if (now - pressedAt < MenuSettleMs)
            {
                return;
            }

            if (ready && (nint)menu == pressedMenu)
            {
                Svc.Log.Information($"{Tag} 按了「退出任務」{MenuSettleMs / 1000} 秒都沒有確認框、選單還開著：關掉選單重來（第 {menuPresses}/{MaxMenuPresses} 次）");
                Callback.Fire(menu, false, -2);
            }

            pressedMenu = 0;
            nextPressAt = now + MenuCooldownMs;
            if (menuPresses >= MaxMenuPresses)
            {
                Disarm(CeFarmStop.ExitTimeout, "menu");
            }

            return;
        }

        if (now < nextPressAt)
        {
            return;
        }

        if (!ready)
        {
            // 可見但沒就緒時先讓它自己載完；卡超過 MenuSettleMs 才再叫一次（同 AutoDuty 的逃生口）。
            if (found && menu->IsVisible)
            {
                if (notReadySince == 0)
                {
                    notReadySince = now;
                }

                if (now - notReadySince < MenuSettleMs)
                {
                    return;
                }
            }

            notReadySince = 0;
            if (now < nextMenuShowAt)
            {
                return;
            }

            nextMenuShowAt = now + MenuRetryMs;
            var agents = AgentModule.Instance();
            var agent = agents == null ? null : agents->GetAgentByInternalId(AgentId.ContentsFinderMenu);
            if (agent != null)
            {
                menuShows++;
                agent->Show();
            }

            return;
        }

        var entries = ReadMenuStrings(menu);
        DumpMenuOnce(entries);
        var first = CeFarmRules.FirstMenuEntry(entries);
        var leave = LeaveEntry();
        if (!CeFarmRules.IsLeaveEntry(first, leave))
        {
            Svc.Log.Information($"{Tag} 副本選單第 0 項是「{first ?? "（沒有字串）"}」，不是 Addon#{LeaveEntryRow}「{leave ?? "（讀不到）"}」：不按、不試別的項；選單字串 {MenuText(entries)}");
            Disarm(CeFarmStop.ExitMenuMismatch, first ?? "-");
            return;
        }

        Callback.Fire(menu, true, 0);
        pressedMenu = (nint)menu;
        pressedAt = now;
        menuPresses++;
        Svc.Log.Information($"{Tag} 副本選單按「退出任務」（第 {menuPresses} 次）");
    }

    // 當幀讀完：GetAtkValueTextOrNull 有界、驗型別，而且只取文字 payload（不會帶出物品連結之類的控制碼）。
    private static unsafe List<string?> ReadMenuStrings(AtkUnitBase* menu)
    {
        var count = Math.Min((int)menu->AtkValuesCount, 32);
        var values = new List<string?>(count);
        for (var i = 0; i < count; i++)
        {
            values.Add(GenericHelpers.GetAtkValueTextOrNull(menu, i));
        }

        return values;
    }

    private static string MenuText(List<string?> values)
    {
        var texts = new List<string>();
        for (var i = 0; i < values.Count; i++)
        {
            if (!string.IsNullOrEmpty(values[i]))
            {
                texts.Add($"[{i}]{values[i]}");
            }
        }

        return texts.Count == 0 ? "（沒有字串）" : string.Join(" ", texts);
    }

    // 診斷：第一次按之前把選單的字串寫一行，改版後選項順序變了查得到。
    private static void DumpMenuOnce(List<string?> values)
    {
        if (menuDumped)
        {
            return;
        }

        menuDumped = true;
        Svc.Log.Information($"{Tag} 副本選單的字串（診斷，載入後只記一次）：{MenuText(values)}");
    }

    private void OnYesnoPreSetup(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonSetupArgs setup)
        {
            return;
        }

        var address = setup.Addon.Address;
        if (claim.NoteSetup(address))
        {
            Svc.Log.Information($"{Tag} 接手的退出確認框還沒按就關了（同一位置又開了一扇確認框），不代按那一扇");
        }

        if (phase != CeFarmPhase.Exiting || !ZoneData.IsInOccultCrescent())
        {
            return;
        }

        var prompt = SelectYesnoSetup.ReadPrompt(setup);
        if (prompt == null)
        {
            Svc.Log.Information($"{Tag} 退出中跳出的確認框讀不到內文，不接手（YesAlready 照常判斷）");
            return;
        }

        var expected = LeavePrompt();
        if (expected == null)
        {
            confirmSeen = true;
            Svc.Log.Information($"{Tag} 讀不到 Addon#{LeavePromptRow} 的退出確認句，確認框留給你或 YesAlready 按：{prompt}");
            return;
        }

        if (prompt.ReplaceLineEndings(string.Empty) != expected)
        {
            Svc.Log.Information($"{Tag} 退出中跳出的確認框不是退出任務，不代按：{prompt}");
            return;
        }

        confirmSeen = true;
        var pause = YesAlreadyBotherPause.Pause(YesnoAddon, YesAlreadyPauseMs);
        if (pause == YesAlreadyBotherPause.Result.Failed)
        {
            Svc.Log.Information($"{Tag} 無法請 YesAlready 暫停確認框，不代按、等它按（{CeFarmRules.ExitTimeLimitMs / 1000} 秒內沒離開新月島就停止）：{prompt}");
            return;
        }

        claim.Claim(address, prompt, Environment.TickCount64 + PressDeadlineMs);
        Svc.Log.Information($"{Tag} 退出確認框建立前就認出（YesAlready：{pause}，它不會碰這一扇），下一幀代按「是」：{prompt}");
    }

    private unsafe void TickPendingPress(long now)
    {
        if (!claim.IsActive)
        {
            return;
        }

        if (claim.IsExpired(now))
        {
            Svc.Log.Information($"{Tag} 退出確認框在時限內已不在或沒準備好（可能已被按掉或關閉），不代按");
            claim.Clear();
            return;
        }

        if (!SelectYesnoSetup.TryGetReadyAt(claim.Address, out var addon))
        {
            return;
        }

        var prompt = GenericHelpers.GetAtkValueTextOrNull(addon, 0);
        var same = claim.Matches((nint)addon, prompt);
        claim.Clear();
        if (!same)
        {
            Svc.Log.Information($"{Tag} 確認框已換成別的句子，不代按：{prompt}");
            return;
        }

        if (phase != CeFarmPhase.Exiting || !ZoneData.IsInOccultCrescent())
        {
            return;
        }

        addon->FireCallbackInt(0);
        Svc.Log.Information($"{Tag} 已代按退出確認框「是」");
    }

    private string? LeavePrompt()
    {
        if (leavePromptLoaded)
        {
            return leavePrompt;
        }

        leavePromptLoaded = true;
        var text = Svc.Data.GetExcelSheet<AddonSheet>().GetRowOrDefault(LeavePromptRow)?.Text.ExtractText();
        leavePrompt = string.IsNullOrEmpty(text) ? null : text.ReplaceLineEndings(string.Empty);
        return leavePrompt;
    }

    private string? LeaveEntry()
    {
        if (leaveEntryLoaded)
        {
            return leaveEntry;
        }

        leaveEntryLoaded = true;
        var text = Svc.Data.GetExcelSheet<AddonSheet>().GetRowOrDefault(LeaveEntryRow)?.Text.ExtractText();
        leaveEntry = string.IsNullOrEmpty(text) ? null : text.ReplaceLineEndings(string.Empty);
        return leaveEntry;
    }

    private void OnTerritoryChanged(ushort id)
    {
        if (phase == CeFarmPhase.Off)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (id == ZoneData.SOUTHHORN)
        {
            EnterReady(now);
            Svc.Log.Information($"{Tag} 進島：第 {roundsDone + 1} 輪待命，等你按「出發」才開始等「{targetName}」");
            Svc.Chat.Print(I18N.T("modules.critical_encounters.farm.messages.ready", new Dictionary<string, string> { ["name"] = targetName, ["round"] = (roundsDone + 1).ToString() }));
            return;
        }

        if (phase is CeFarmPhase.ExitDelay or CeFarmPhase.Exiting)
        {
            roundsDone++;
            Svc.Log.Information($"{Tag} 第 {roundsDone} 輪完成（已離開新月島，{RoundsText(roundLimit)}）");
            if (CeFarmRules.RoundsDone(roundsDone, roundLimit))
            {
                Disarm(CeFarmStop.RoundsDone);
                return;
            }
        }
        else if (phase is not (CeFarmPhase.Away or CeFarmPhase.Ready))
        {
            Svc.Log.Information($"{Tag} 第 {roundsDone + 1} 輪還沒打完就離開了新月島（{phase}）：這一輪不算，等下一次進島");
        }

        SetPhase(CeFarmPhase.Away, now);
        entry = null;
        ResetExitUi();
    }

    private void OnLogout(int type, int code)
    {
        Disarm(CeFarmStop.LoggedOut);
    }

    // 不在戰鬥、詠唱、讀圖、對話或過場中，角色可以選取。
    private static bool IsFree()
    {
        var condition = Svc.Condition;
        if (condition[ConditionFlag.InCombat] || condition[ConditionFlag.Casting] || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]
            || condition[ConditionFlag.OccupiedInEvent] || condition[ConditionFlag.OccupiedInQuestEvent] || condition[ConditionFlag.OccupiedInCutSceneEvent]
            || condition[ConditionFlag.Occupied] || condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.WatchingCutscene78])
        {
            return false;
        }

        return Svc.Objects.LocalPlayer?.IsTargetable == true;
    }

    // 同一個世界的小隊在 PartyList；跨服小隊（招募板組的團）看條件旗標。
    private static bool InParty()
    {
        return Svc.Party.Length > 0 || Svc.Condition[ConditionFlag.ParticipatingInCrossWorldPartyOrAlliance];
    }

    private static string RoundsText(int limit)
    {
        return limit > 0
            ? I18N.T("modules.critical_encounters.farm.rounds_limit", new Dictionary<string, string> { ["limit"] = limit.ToString() })
            : I18N.T("modules.critical_encounters.farm.rounds_unlimited");
    }

    private string ReasonText(CeFarmStop stop, string? detail)
    {
        var args = new Dictionary<string, string>
        {
            ["name"] = targetName,
            ["detail"] = detail ?? string.Empty,
            ["minutes"] = stop == CeFarmStop.WaitTimeout ? detail ?? string.Empty : (CeFarmRules.JoinedTimeLimitMs / 60_000).ToString(),
            ["seconds"] = (CeFarmRules.ExitTimeLimitMs / 1000).ToString(),
            ["done"] = roundsDone.ToString(),
            ["expected"] = leaveEntry ?? "?",
        };
        var key = stop switch
        {
            CeFarmStop.StopAll => "stop_all",
            CeFarmStop.LeftParty => "left_party",
            CeFarmStop.RoundsDone => "rounds_done",
            CeFarmStop.Dead => "dead",
            CeFarmStop.WaitTimeout => "wait_timeout",
            CeFarmStop.CeFailed => "ce_failed",
            CeFarmStop.CeUnknown => "ce_unknown",
            CeFarmStop.NotParticipated => "not_participated",
            CeFarmStop.JoinedTooLong => "joined_too_long",
            CeFarmStop.ExitTimeout => detail switch
            {
                "confirm" => "exit_confirm",
                "menu" => "exit_menu",
                "busy" => "exit_busy",
                _ => "exit_timeout",
            },
            CeFarmStop.ExitMenuMismatch => "exit_menu_mismatch",
            CeFarmStop.LoggedOut => "logged_out",
            CeFarmStop.ModuleDisabled => "module_disabled",
            _ => detail == null ? "manual" : "error",
        };
        return I18N.T("modules.critical_encounters.farm.reason." + key, args);
    }

    private static string BlockKey(CeFarmArmBlock block)
    {
        return block switch
        {
            CeFarmArmBlock.NoTarget => "modules.critical_encounters.farm.block.no_target",
            CeFarmArmBlock.ModuleDisabled => "modules.critical_encounters.farm.block.module_disabled",
            CeFarmArmBlock.NotInParty => "modules.critical_encounters.farm.block.not_in_party",
            _ => "modules.critical_encounters.farm.block.movement_running",
        };
    }

    /// <summary>主視窗「跟團刷 CE」能不能按武裝；不能時回原因（給說明框用）。</summary>
    public string? ArmBlockText()
    {
        var block = CeFarmRules.CanArm(module.Config.CeFarmTarget, module.IsEnabled, InParty(), module.Plugin.Automation?.IsAnyMovementRunning() == true);
        return block == CeFarmArmBlock.None ? null : I18N.T(BlockKey(block));
    }

    private static readonly Dictionary<uint, string> Names = [];

    /// <summary>緊急遭遇戰名稱（DynamicEvent 表，查不到時 #id）；第一次查才讀表。</summary>
    public static string CeName(uint id)
    {
        if (Names.TryGetValue(id, out var name))
        {
            return name;
        }

        name = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.DynamicEvent>().GetRowOrDefault(id)?.Name.ExtractText() ?? string.Empty;
        if (name.Length == 0)
        {
            name = $"#{id}";
        }

        Names[id] = name;
        return name;
    }
}
