using System;
using BOCCHI.Data;
using BOCCHI.Modules.StateManager;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Ocelot.Modules;

namespace BOCCHI.Modules.Teleporter;

// 本檔契約：返回確認框只在 ReturnChain 剛施放「返回」之後的時窗內、問句比中 Addon#118 時才代按。
// 在確認框 PreSetup 認框，認得才請 YesAlready 暫停 SelectYesno（它只在 PostSetup 判斷一次）並記下那一扇的位址；
// 按鍵延到下一幀，只按位址相同、問句相同的那一扇。同幀兩個插件各按一次＝第二次對已關閉的視窗回呼。
// 全部跑在 framework 執行緒（AddonLifecycle、Framework.Update、動作鏈任務）。
[OcelotModule(2)]
public class TeleporterModule : Module
{
    private const string YesnoAddon = SelectYesnoSetup.AddonName;

    /// <summary>Addon#118＝「確定要回到「地名」的開始地點嗎？」，新月島裡用「返回」時跳出的確認框。</summary>
    private const uint ReturnPromptAddonRow = 118;

    private const int ReturnPromptWindowMs = 10_000;

    private const int PressDeadlineMs = 2_000;

    private const int YesAlreadyPauseMs = 3_000;

    public override TeleporterConfig Config
    {
        get => PluginConfig.TeleporterConfig;
    }

    public override bool ShouldInitialize
    {
        get => true;
    }

    public readonly Teleporter teleporter;

    private ReturnPromptMatcher? returnPrompt;

    private bool returnPromptLoaded;

    // Environment.TickCount64 的時刻；0＝沒有在等。
    private long returnPromptArmedUntil;

    private readonly YesnoClaim claim = new();

    private readonly ReturnPressGate pressGate = new();

    public TeleporterModule(Plugin plugin, Config config)
        : base(plugin, config)
    {
        teleporter = new Teleporter(this);

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreSetup, YesnoAddon, OnSelectYesnoPreSetup);
        Svc.Framework.Update += OnFrameworkUpdate;
    }

    public override void Initialize()
    {
        var states = GetModule<StateManagerModule>();
        states.OnEnterInFate += teleporter.OnFateEnter;
        states.OnExitInFate += teleporter.OnFateEnd;
        states.OnExitInCriticalEncounter += teleporter.OnCriticalEncounterEnd;
    }

    public override void Dispose()
    {
        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreSetup, YesnoAddon, OnSelectYesnoPreSetup);

        var states = GetModule<StateManagerModule>();
        states.OnEnterInFate -= teleporter.OnFateEnter;
        states.OnExitInFate -= teleporter.OnFateEnd;
        states.OnExitInCriticalEncounter -= teleporter.OnCriticalEncounterEnd;
    }

    public bool IsReady()
    {
        return teleporter.IsReady();
    }

    /// <summary>ReturnChain 施放「返回」前呼叫：接下來一段時間內跳出的返回確認框才會被代按。</summary>
    public void ExpectReturnPrompt()
    {
        returnPromptArmedUntil = Environment.TickCount64 + ReturnPromptWindowMs;
    }

    /// <summary>返回確認框還沒出現（代按時窗開著、還沒比中）；比中、收回或過了時窗都是 false。</summary>
    public bool AwaitingReturnPrompt
    {
        get => returnPromptArmedUntil != 0 && Environment.TickCount64 <= returnPromptArmedUntil;
    }

    /// <summary>自動化停止時呼叫：收回代按時窗與排在下一幀的代按，確認框留給使用者自己按。</summary>
    public void CancelReturnPrompt()
    {
        var armed = returnPromptArmedUntil != 0 && Environment.TickCount64 <= returnPromptArmedUntil;
        returnPromptArmedUntil = 0;
        if (!armed && !claim.IsActive)
        {
            return;
        }

        ClearPending();
        Svc.Log.Information("[BOCCHI][Teleporter] 自動化已停止，收回返回確認框的代按。");
    }

    private void OnSelectYesnoPreSetup(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonSetupArgs setup)
        {
            return;
        }

        var address = setup.Addon.Address;
        if (claim.NoteSetup(address))
        {
            Svc.Log.Information("[BOCCHI][Teleporter] 接手的返回確認框還沒按就關了（同一位置又開了一扇確認框），不代按那一扇。");
        }

        if (!ZoneData.IsInOccultCrescent() || ZoneData.IsInForkedTower() || Player.IsDead)
        {
            return;
        }

        var prompt = SelectYesnoSetup.ReadPrompt(setup);
        if (returnPromptArmedUntil == 0 || Environment.TickCount64 > returnPromptArmedUntil)
        {
            // 舊版對「AtkValues[7] 是 -1」的確認框一律按「是」；留一行紀錄，改版後少按了什麼查得到。
            if (prompt != null && SelectYesnoSetup.IsLegacyAutoYes(setup))
            {
                Svc.Log.Information($"[BOCCHI][Teleporter] 這個確認框舊版會自動按「是」，現在只代按返回動作鏈自己的確認框：{prompt}");
            }

            return;
        }

        if (prompt == null)
        {
            Svc.Log.Information("[BOCCHI][Teleporter] 返回動作進行中跳出的確認框讀不到內文，不接手（YesAlready 照常判斷）。");
            return;
        }

        var matcher = GetReturnPromptMatcher();
        if (matcher == null || !matcher.IsMatch(prompt))
        {
            Svc.Log.Information($"[BOCCHI][Teleporter] 返回動作進行中跳出的確認框不是返回確認，不代按：{prompt}");
            return;
        }

        returnPromptArmedUntil = 0;

        var pause = YesAlreadyBotherPause.Pause(YesnoAddon, YesAlreadyPauseMs);
        if (pause == YesAlreadyBotherPause.Result.Failed)
        {
            Svc.Log.Information($"[BOCCHI][Teleporter] 無法請 YesAlready 暫停確認框，這次不代按「是」、留給你自己按：{prompt}");
            return;
        }

        claim.Claim(address, prompt, Environment.TickCount64 + PressDeadlineMs);
        pressGate.Reset();
        Svc.Log.Information($"[BOCCHI][Teleporter] 返回確認框建立前就認出（YesAlready：{pause}，它不會碰這一扇），下一幀代按「是」：{prompt}");
    }

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        teleporter.TickFateEndConfirm();
        teleporter.TickPotWait();
        if (!claim.IsActive)
        {
            return;
        }

        if (claim.IsExpired(Environment.TickCount64))
        {
            Svc.Log.Information("[BOCCHI][Teleporter] 返回確認框在時限內已不在或沒準備好（可能已被按掉或關閉），不代按。");
            ClearPending();
            return;
        }

        if (!SelectYesnoSetup.TryGetReadyAt(claim.Address, out var addon))
        {
            return;
        }

        // PressDeadlineMs（2 秒）比 ReturnPressGate.MaxWaitMs 長，等的期間代按不會過期；YesAlready 已在 PostSetup 放過這一扇、不會再判斷。
        if (!pressGate.Ready(Environment.TickCount64, Svc.Objects.LocalPlayer?.IsCasting == true, ReadAnimationLock()))
        {
            return;
        }

        var prompt = GenericHelpers.GetAtkValueTextOrNull(addon, 0);
        var same = claim.Matches((nint)addon, prompt);
        ClearPending();
        if (!same)
        {
            Svc.Log.Information($"[BOCCHI][Teleporter] 確認框已換成別的句子，不代按：{prompt}");
            return;
        }

        if (!ZoneData.IsInOccultCrescent() || ZoneData.IsInForkedTower() || Player.IsDead)
        {
            return;
        }

        addon->FireCallbackInt(0);
        if (!pressGate.Waited)
        {
            Svc.Log.Information($"[BOCCHI][Teleporter] 已代按返回確認框「是」：{prompt}");
        }
        else
        {
            Svc.Log.Information($"[BOCCHI][Teleporter] 代按返回確認框「是」前等了 {pressGate.WaitedMs / 1000f:F2} 秒"
                                + $"（比中確認框時 {(pressGate.FirstCasting ? "正在詠唱、" : string.Empty)}動作硬直剩 {pressGate.FirstLock:F2} 秒；"
                                + $"{(pressGate.TimedOut ? $"等滿 {ReturnPressGate.MaxWaitMs / 1000f:F0} 秒還沒結束，照樣按" : "結束後才按")}）：硬直中按下去不會開始詠唱返回。");
        }
    }

    // ActionManager+0x08＝動作硬直剩餘秒數；位址解不出來時當作沒有硬直（照舊立刻按）。
    private static unsafe float ReadAnimationLock()
    {
        if (ActionManager.Addresses.Instance.Value == 0)
        {
            return 0f;
        }

        var manager = ActionManager.Instance();
        return manager == null ? 0f : manager->AnimationLock;
    }

    private void ClearPending()
    {
        claim.Clear();
    }

    private ReturnPromptMatcher? GetReturnPromptMatcher()
    {
        if (returnPromptLoaded)
        {
            return returnPrompt;
        }

        returnPromptLoaded = true;
        var row = Svc.Data.GetExcelSheet<Addon>().GetRowOrDefault(ReturnPromptAddonRow);
        returnPrompt = row == null ? null : ReturnPromptMatcher.FromSeString(row.Value.Text);

        Svc.Log.Information(returnPrompt == null
            ? $"[BOCCHI][Teleporter] 讀不到 Addon#{ReturnPromptAddonRow} 的返回確認句，返回的確認框一律留給你自己按。"
            : $"[BOCCHI][Teleporter] 返回確認句範本（Addon#{ReturnPromptAddonRow}）：{string.Join(" … ", returnPrompt.Segments)}");

        return returnPrompt;
    }
}
