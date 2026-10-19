using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using BOCCHI.ActionHelpers;
using BOCCHI.Automation;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.Buff;
using BOCCHI.Modules.Buff.Chains;
using BOCCHI.Modules.Teleporter;
using BOCCHI.Pathfinding;
using Dalamud.Game.ClientState.Conditions;
using ECommons;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Ocelot;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;
using Ocelot.IPC;

namespace BOCCHI.Chains;

public class ReturnChain(TeleporterModule module, ReturnChainConfig config) : RetryChainFactory
{
    private const int DismountTimeLimitMs = 5000;

    private const int CastStartTimeLimitMs = 5000;

    private const int CastEndTimeLimitMs = 8000;

    private const int LoadingTimeLimitMs = 5000;

    private const int CombatWaitLimitMs = (int)(HuntCombatWait.MaxWaitMs + HuntCombatWait.SettleMs) + 5000;

    private bool complete = false;

    // 戰鬥中原地等（同巡迴）：整條返回共用一份，跨嘗試保留。
    private readonly HuntCombatWait combatWait = new();

    // 讓給玩家、這條不再施放：外殼照「完成」收場（呼叫端與重試用完時一樣往下走）。
    private bool yielded;

    /// <summary>插件載入後返回失敗（逾時或倒地，不含被中止）的總次數；只在 framework 執行緒讀寫，活動帳取差值。</summary>
    public static int FailureCount { get; private set; }

    /// <summary>BOCCHI 自己施放的「返回」正在等詠唱開始或詠唱中；動作鏈被中止就清掉。只在 framework 執行緒讀寫。</summary>
    public static bool OwnCastInFlight { get; private set; }

    /// <summary>返回被玩家接手後的讓位，所有返回動作鏈共用；只在 framework 執行緒讀寫。</summary>
    public static ReturnTakeover Takeover { get; } = new();

    private TeleporterModule Module
    {
        get => module;
    }

    protected override Chain Create(Chain chain)
    {
        chain.BreakIf(() => Player.IsDead);

        var shouldReturn = GetCostToReturn() < GetCostToWalk();

        if (shouldReturn)
        {
            // 騎著坐騎施放返回常被打斷，而且「有在詠唱」不代表詠唱的是返回（可能是坐騎）：先下坐騎，只認返回本身的詠唱。
            var watch = new ReturnAttempt(this, attempt, GetMaxAttempts(), TryGetVnav());
            chain.Then(new TaskManagerTask(watch.WaitOutCombat, "ReturnChain.WaitOutCombat", new TaskManagerConfiguration { TimeLimitMS = CombatWaitLimitMs }));
            // 沒在讓位、你也沒在操作時第一拍就往下（同沒有這一步）。
            chain.Then(new TaskManagerTask(watch.WaitForPlayer, "ReturnChain.WaitForPlayer",
                new TaskManagerConfiguration { TimeLimitMS = (int)ReturnTakeover.WaitLimitMs + 5000 + CombatWaitLimitMs }));
            chain.BreakIf(() => yielded);

            chain.Then(new TaskManagerTask(watch.Dismount, "ReturnChain.Dismount", new TaskManagerConfiguration { TimeLimitMS = DismountTimeLimitMs }));
            chain.Then(_ => watch.WaitCooldown());
            chain.Then(_ => watch.StopNavigationBeforeCast());
            // 在施放前一刻才開時窗：確認框的代按只認這條動作鏈自己施放的「返回」。
            chain.Then(_ => module.ExpectReturnPrompt());
            chain.Then(_ => watch.Cast());
            chain.Then(new TaskManagerTask(watch.WaitForReturnCast, "ReturnChain.WaitForReturnCast", new TaskManagerConfiguration { TimeLimitMS = CastStartTimeLimitMs }));
            chain.Then(new TaskManagerTask(watch.WaitForCastEnd, "ReturnChain.WaitForCastEnd", new TaskManagerConfiguration { TimeLimitMS = CastEndTimeLimitMs }));
            chain.Then(new TaskManagerTask(watch.WaitLoadingStart, "ReturnChain.WaitLoadingStart", new TaskManagerConfiguration { TimeLimitMS = LoadingTimeLimitMs }));
            chain.Then(new TaskManagerTask(watch.WaitLoadingEnd, "ReturnChain.WaitLoadingEnd", new TaskManagerConfiguration { TimeLimitMS = LoadingTimeLimitMs }));
            chain.Then(_ => watch.Done());
            chain.OnCancel(watch.ReportFailure);
        }

        chain.Then(ChainHelper.TreasureSightChain());
        chain.Then(ApplyBuffs);

        if (config.ApproachAetheryte)
        {
            var vnav = module.GetIPCSubscriber<VNavmesh>();
            var lifestream = module.GetIPCSubscriber<Lifestream>();
            var position = GetAetherytePosition();

            chain.Then(new ApproachAethernetChain(vnav, position));
            chain.Then(_ => lifestream.GetActiveCustomAetheryte() != 0 && Player.DistanceTo(position) <= AethernetData.DISTANCE);
            chain.Then(_ => vnav.Stop());
        }


        return chain.Then(_ => complete = true);
    }

    private Chain ApplyBuffs()
    {
        var vnav = module.GetIPCSubscriber<VNavmesh>();
        var buffs = module.GetModule<BuffModule>();

        var closestKnowledgeCrystal = ZoneData.GetNearbyKnowledgeCrystal(60f).FirstOrDefault();

        var chain = Chain.Create();
        chain.BreakIf(() => !buffs.ShouldRefreshBuffs() || !vnav.IsReady() || closestKnowledgeCrystal == null);
        if (closestKnowledgeCrystal == null)
        {
            return chain;
        }

        chain.Then(_ => Actions.TryUnmount());

        // 走到水晶旁才換得了職業：停下卻不夠近時重走（有上限），見 ChainHelper.PathfindToAndWait。
        var crystalPosition = closestKnowledgeCrystal.Position;
        chain.Then(ChainHelper.PathfindToAndWait(crystalPosition, AethernetData.DISTANCE, TravelSource.KnowledgeCrystal, null, YieldEnabled), new TaskManagerConfiguration { TimeLimitMS = 40000 });
        chain.Then(_ => vnav.Stop());

        chain.Then(new AllBuffsChain(buffs));

        return chain;
    }

    public override bool IsComplete()
    {
        return complete || yielded;
    }

    /// <summary>這一次返回讓給使用者操作了（沒有施放）：呼叫端這一趟不要再替他移動。外殼收場後讀。</summary>
    public bool Yielded
    {
        get => yielded;
    }

    private void YieldToPlayer(int number, string reason)
    {
        yielded = true;
        var line = $"[BOCCHI][返回讓位] 第 {number} 次嘗試：{reason}，這次不返回，交給你操作。";
        if (Takeover.TakeGiveUpReport())
        {
            Svc.Log.Information(line);
        }
        else
        {
            Svc.Log.Debug(line);
        }
    }

    // 施放前問「你在不在操作」時讀當下按鍵用；只在 framework 執行緒用。
    private static readonly HandsOnProbe PreCastProbe = new();

    // 返回會被自動化與巡迴兩邊叫用：任一邊開著「讓位給手動移動」就讓位；施放前的問與走去水晶共用這一個判斷。
    private bool YieldEnabled()
    {
        var plugin = module.PluginConfig;
        return plugin.AutomatorConfig.YieldToManualMovement || plugin.PathfinderConfig.YieldToManualMovement;
    }

    // 兩個讓位設定都關著時不問（同沒有這個判斷）。回 true＝當成已被接手，接著照返回讓位等你停下。
    private bool TakeOverIfHandsOn(long now, int number)
    {
        if (!YieldEnabled())
        {
            return false;
        }

        var pressed = PreCastProbe.ReadInput(now);
        var recent = HandsOnRecent.Within(now, out var what);
        if (!Takeover.TakeOverIfHandsOn(now, pressed || recent))
        {
            return false;
        }

        var condition = Svc.Condition;
        Svc.Log.Information($"[BOCCHI][返回讓位] 第 {number} 次嘗試：返回前偵測到你在操作（{(pressed ? "按了移動鍵" : what)}，{HandsOnRecent.WindowMs / 1000f:F1} 秒內）：先不下坐騎、不施放；"
                            + $"你停下（沒騎坐騎、靜止 {ReturnTakeover.QuietMs / 1000f:F1} 秒）就接著返回，騎著坐騎或 {ReturnTakeover.WaitLimitMs / 1000} 秒內沒停下就這次不返回。"
                            + $"騎乘={(condition[ConditionFlag.Mounted] ? "是" : "否")} 角色位置 {Player.Position:F1}");
        return true;
    }

    public override int GetMaxAttempts()
    {
        return 5;
    }

    // 呼叫端包這條的那一步：原本 60 秒，加上戰鬥中原地等的上限，免得等戰鬥時整趟被中止。
    public override TaskManagerConfiguration? Config()
    {
        return new TaskManagerConfiguration { TimeLimitMS = 60000 + CombatWaitLimitMs };
    }

    // 每次嘗試施放前（含等玩家停下時）每一拍呼叫；回 true＝正在等戰鬥結束、這一拍不往下。
    private bool HoldForCombat(long now, int number, ReturnAttempt attempt)
    {
        var step = combatWait.Observe(Svc.Condition[ConditionFlag.InCombat], now);
        Takeover.Hold(step.HeldMs);
        switch (step.Event)
        {
            case HuntCombatEvent.Started:
                var stopped = attempt.StopOwnNavigation();
                Svc.Log.Information($"[BOCCHI][返回] 第 {number} 次嘗試：戰鬥中，先不施放返回：{stopped}原地等戰鬥結束（讓戰鬥 AI 或你自己打完；這段期間不判斷是不是你在操作），"
                                    + $"脫離戰鬥 {HuntCombatWait.SettleMs / 1000f:F1} 秒後再施放；最多等 {HuntCombatWait.MaxWaitMs / 1000} 秒。角色位置 {Player.Position:F1}");
                break;
            case HuntCombatEvent.Ended:
                Svc.Log.Information($"[BOCCHI][返回] 第 {number} 次嘗試：脫離戰鬥 {HuntCombatWait.SettleMs / 1000f:F1} 秒，這次等了 {step.WaitedMs / 1000f:F1} 秒，接著施放返回。角色位置 {Player.Position:F1}");
                break;
            case HuntCombatEvent.Expired:
                Svc.Log.Information($"[BOCCHI][返回] 第 {number} 次嘗試：等了 {HuntCombatWait.MaxWaitMs / 1000} 秒還在戰鬥：這場戰鬥照原本的做法施放返回（戰鬥中多半放不出去），脫離戰鬥後下一場再原地等。");
                break;
            case HuntCombatEvent.ExpiredOver:
                Svc.Log.Debug($"[BOCCHI][返回] 照原本做法的那場戰鬥結束了（從開始等算起 {step.WaitedMs / 1000f:F1} 秒），之後再進入戰鬥照樣原地等。");
                break;
        }

        return combatWait.Holding;
    }

    private Vector3 GetAetherytePosition()
    {
        if (ZoneData.Aetherytes.TryGetValue(Svc.ClientState.TerritoryType, out var position))
        {
            return position;
        }

        throw new Exception("Unable to determine Aetheryte position");
    }

    private float GetCostToReturn()
    {
        if (ZoneData.StartingLocations.TryGetValue(Svc.ClientState.TerritoryType, out var start))
        {
            return Vector3.Distance(start, GetAetherytePosition()) + 75f;
        }


        throw new Exception("Unable to determine Starting position");
    }

    private float GetCostToWalk()
    {
        return Player.DistanceTo(GetAetherytePosition());
    }

    private VNavmesh? TryGetVnav()
    {
        try
        {
            return module.GetIPCSubscriber<VNavmesh>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>一次嘗試的返回施放：各步驟由動作鏈在 framework 執行緒呼叫；只存數值，不存遊戲物件。</summary>
    /// <param name="vnav">記錄失敗原因用（施放後 vnavmesh 有沒有在走、是誰的路線）；null＝不問。</param>
    private sealed class ReturnAttempt(ReturnChain owner, int number, int maxAttempts, VNavmesh? vnav)
    {
        private bool waitBegun;

        // 施放返回之後看到的事（失敗那一行的「原因」欄）；施放前的不算。
        private readonly ReturnInterruptions seen = new();

        private readonly OwnMoveDestinations own = new();

        private Vector3 castFrom;

        private bool castFromKnown;

        private ReturnStage stage = ReturnStage.Dismount;

        // 第一步還沒跑就被收掉（例如建鏈時已倒地）不算一次返回。
        private bool started;

        private long stageAt = Environment.TickCount64;

        private long lastDismountTryAt;

        private int dismountTries;

        private string dismount = "不需要";

        private bool mountedAtCast;

        private bool combatAtCast;

        private ReturnCastKind seenCast = ReturnCastKind.None;

        private byte otherCastType;

        private uint otherCastId;

        private long castSeenAt;

        private long castEndedAt;

        private long sentAt;

        private float gcdAtCast;

        // 公共冷卻轉完的時刻；還在轉是 0。
        private long gcdQuietSince;

        private int resends;

        /// <summary>每次嘗試的第一步：戰鬥中原地等到脫離戰鬥 <see cref="HuntCombatWait.SettleMs"/>，沒在戰鬥就當場往下。</summary>
        public bool WaitOutCombat()
        {
            return !owner.HoldForCombat(Environment.TickCount64, number, this);
        }

        /// <returns>給記錄用的前綴；BOCCHI 自己送的（或讀不到是誰的）路線才停，別人或 Lifestream 的不停。</returns>
        /// <remarks>還在算的尋路一起取消：只停路徑的話，背景算完的路會在詠唱途中把角色帶走。</remarks>
        public string StopOwnNavigation()
        {
            var running = QueryVnavRunning();
            var computing = QueryVnavComputing();
            if (!running && !computing)
            {
                return string.Empty;
            }

            if (running && JudgeVnavOwner() == OwnPathVerdict.Foreign)
            {
                return "vnavmesh 在走的不是 BOCCHI 的路線，不停它；";
            }

            try
            {
                if (computing)
                {
                    vnav!.CancelAllPathfinds();
                }

                if (running)
                {
                    vnav!.Stop();
                }
            }
            catch (Exception)
            {
                return "停不了 vnavmesh（IPC 沒回應），";
            }

            return "停下 BOCCHI 自己的導航，";
        }

        /// <summary>
        /// 返回剛被玩家接手過、或施放前最近 <see cref="HandsOnRecent.WindowMs"/> 內你在操作：等他停下，騎著坐騎或等太久就讓整條收掉（不拉他下坐騎）。
        /// 兩者都不是時第一拍就往下。戰鬥中原地等，不算進等待時間。
        /// </summary>
        public bool WaitForPlayer()
        {
            var now = Environment.TickCount64;
            if (!waitBegun)
            {
                if (!Takeover.Active(now) && !owner.TakeOverIfHandsOn(now, number))
                {
                    return true;
                }

                waitBegun = true;
                Takeover.BeginWait(now);
            }

            if (owner.HoldForCombat(now, number, this))
            {
                return false;
            }

            var player = Svc.Objects.LocalPlayer;
            if (player == null)
            {
                return false;
            }

            var condition = Svc.Condition;
            var mounted = condition[ConditionFlag.Mounted] || condition[ConditionFlag.MountOrOrnamentTransition] || condition[ConditionFlag.Mounting] || condition[ConditionFlag.Mounting71]
                          || ReturnCast.Classify(player.IsCasting, player.CastActionType, player.CastActionId) == ReturnCastKind.Mount;
            var busy = condition[ConditionFlag.Casting] || condition[ConditionFlag.Casting87] || condition[ConditionFlag.Jumping] || condition[ConditionFlag.Jumping61]
                       || IsLoading() || QueryVnavRunning();
            switch (Takeover.Step(now, player.Position, mounted, busy))
            {
                case ReturnHoldDecision.Wait:
                    return false;
                case ReturnHoldDecision.Settled:
                    Svc.Log.Information($"[BOCCHI][返回讓位] 第 {number} 次嘗試：你停下了（沒騎坐騎、靜止 {ReturnTakeover.QuietMs / 1000f:F1} 秒），接著施放返回。");
                    return true;
                case ReturnHoldDecision.GiveUpMounted:
                    owner.YieldToPlayer(number, "你騎著坐騎（不拉你下來）");
                    return true;
                case ReturnHoldDecision.GiveUpTimeout:
                    owner.YieldToPlayer(number, $"{ReturnTakeover.WaitLimitMs / 1000} 秒內你都沒停下");
                    return true;
                default:
                    return true;
            }
        }

        public bool Dismount()
        {
            var now = Environment.TickCount64;
            if (!started)
            {
                started = true;
                stageAt = now;
            }

            var condition = Svc.Condition;
            var mounted = condition[ConditionFlag.Mounted];
            var player = Svc.Objects.LocalPlayer;
            var castingMount = player != null && ReturnCast.Classify(player.IsCasting, player.CastActionType, player.CastActionId) == ReturnCastKind.Mount;
            var done = ReturnCast.DismountStep(mounted, condition[ConditionFlag.MountOrOrnamentTransition], castingMount, now - stageAt, now - lastDismountTryAt, out var tryNow, out var gaveUp);
            if (tryNow)
            {
                // 兩種下坐騎的寫法輪流送：坐騎 0 與一般動作「跳下」，任一種沒反應時另一種補上。
                if (dismountTries++ % 2 == 0)
                {
                    Actions.TryUnmount();
                }
                else
                {
                    Actions.Dismount.Cast();
                }

                lastDismountTryAt = now;
            }

            if (!done)
            {
                return false;
            }

            if (gaveUp)
            {
                dismount = "沒成功";
                Svc.Log.Information($"[BOCCHI][返回] 第 {number} 次嘗試：{ReturnCast.DismountGiveUpMs / 1000} 秒內沒下坐騎（試了 {dismountTries} 次），照樣施放返回。");
            }
            else if (dismountTries > 0)
            {
                dismount = "成功";
            }

            Enter(ReturnStage.WaitCooldown, now);
            return true;
        }

        public bool WaitCooldown()
        {
            return Actions.Return.CanCast();
        }

        public void Cast()
        {
            var condition = Svc.Condition;
            mountedAtCast = condition[ConditionFlag.Mounted];
            combatAtCast = condition[ConditionFlag.InCombat];
            if (Svc.Objects.LocalPlayer is { } player)
            {
                castFrom = player.Position;
                castFromKnown = true;
            }

            gcdAtCast = Actions.OccultReturn.GetRecastTime();
            sentAt = Environment.TickCount64;
            Enter(ReturnStage.WaitCastStart, sentAt);
            Actions.Return.Cast();
        }

        public bool WaitForReturnCast()
        {
            var now = Environment.TickCount64;
            if (IsLoading())
            {
                Enter(ReturnStage.Loading, now);
                return true;
            }

            var player = Svc.Objects.LocalPlayer;
            if (player == null)
            {
                return false;
            }

            var kind = ReturnCast.Classify(player.IsCasting, player.CastActionType, player.CastActionId);
            Observe(player.Position, kind);
            if (kind is (ReturnCastKind.Mount or ReturnCastKind.Other) && seenCast == ReturnCastKind.None)
            {
                otherCastType = player.CastActionType;
                otherCastId = player.CastActionId;
            }

            seenCast = ReturnCast.Remember(seenCast, kind);
            if (kind != ReturnCastKind.Return)
            {
                TryResend(now, kind);
                return false;
            }

            castSeenAt = now;
            Enter(ReturnStage.Casting, now);
            return true;
        }

        private void TryResend(long now, ReturnCastKind kind)
        {
            var gcd = Actions.OccultReturn.GetRecastTime();
            if (gcd > 0f)
            {
                gcdQuietSince = 0;
            }
            else if (gcdQuietSince == 0)
            {
                gcdQuietSince = now;
            }

            var condition = Svc.Condition;
            var module = owner.Module;
            if (!ReturnCast.ShouldResend(now - sentAt, gcdQuietSince == 0 ? -1 : now - gcdQuietSince, kind != ReturnCastKind.None, !module.AwaitingReturnPrompt,
                    seen.MovedMeters >= ReturnInterruptions.MovedThresholdM, condition[ConditionFlag.InCombat], condition[ConditionFlag.Mounted], resends)
                || IsYesnoOpen())
            {
                return;
            }

            resends++;
            Svc.Log.Information($"[BOCCHI][返回] 第 {number} 次嘗試：送出 {(now - sentAt) / 1000f:F1} 秒後沒看到確認框也沒開始詠唱（送出時公共冷卻剩 {gcdAtCast:F2} 秒），"
                                + $"冷卻已轉完：再送一次（補送 {resends}/{ReturnCast.MaxResends}）。");
            sentAt = now;
            module.ExpectReturnPrompt();
            Actions.Return.Cast();
        }

        private static unsafe bool IsYesnoOpen()
        {
            return GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var addon) && addon->IsVisible;
        }

        public bool WaitForCastEnd()
        {
            var now = Environment.TickCount64;
            if (IsLoading())
            {
                castEndedAt = now;
                Enter(ReturnStage.Loading, now);
                return true;
            }

            var player = Svc.Objects.LocalPlayer;
            var kind = player == null ? ReturnCastKind.None : ReturnCast.Classify(player.IsCasting, player.CastActionType, player.CastActionId);
            if (player != null)
            {
                Observe(player.Position, kind);
            }

            if (kind == ReturnCastKind.Return)
            {
                return false;
            }

            castEndedAt = now;
            Enter(ReturnStage.WaitLoadingStart, now);
            return true;
        }

        public bool WaitLoadingStart()
        {
            if (!IsLoading())
            {
                if (Svc.Objects.LocalPlayer is { } player)
                {
                    Observe(player.Position, ReturnCast.Classify(player.IsCasting, player.CastActionType, player.CastActionId));
                }

                return false;
            }

            Enter(ReturnStage.Loading, Environment.TickCount64);
            return true;
        }

        public bool WaitLoadingEnd()
        {
            return !Svc.Condition[ConditionFlag.BetweenAreas];
        }

        public void Done()
        {
            Enter(ReturnStage.Done, Environment.TickCount64);
            Takeover.Clear();
        }

        public void ReportFailure()
        {
            OwnCastInFlight = false;
            if (!started || !ReturnCast.IsFailureStage(stage))
            {
                return;
            }

            var now = Environment.TickCount64;
            var ending = ReturnCast.Ending(Player.IsDead, now - stageAt, Limit(stage));
            if (ending != "中止")
            {
                FailureCount++;
            }

            var cast = seenCast is ReturnCastKind.Mount or ReturnCastKind.Other && otherCastId != 0
                ? $"{ReturnCast.Label(seenCast)}:{otherCastType}/{otherCastId}"
                : ReturnCast.Label(seenCast);
            var castSeconds = castSeenAt == 0 ? "?" : (((castEndedAt == 0 ? now : castEndedAt) - castSeenAt) / 1000f).ToString("F2", CultureInfo.InvariantCulture);
            var condition = Svc.Condition;
            Svc.Log.Information(
                $"[BOCCHI][返回失敗] 嘗試={number}/{maxAttempts} 階段={ReturnCast.Label(stage)} 收場={ending} 詠唱={cast} 詠唱秒={castSeconds} "
                + $"下坐騎={dismount} 施放時騎乘={YesNo(mountedAtCast)} 施放時戰鬥={YesNo(combatAtCast)} 現在騎乘={YesNo(condition[ConditionFlag.Mounted])} 本階段秒={(now - stageAt) / 1000f:F1} "
                + $"原因={seen.Cause()} 移動米={seen.MovedLabel()} vnav={seen.VnavLabel()} 送出時公共冷卻秒={gcdAtCast:F2} 補送={resends}");

            if (ending == "逾時" && seen.Combat && seen.MovedMeters >= ReturnInterruptions.MovedThresholdM)
            {
                Svc.Log.Information($"[BOCCHI][返回] 第 {number} 次返回在戰鬥中被打斷（{seen.Cause()}）：戰鬥中的移動多半是戰鬥 AI 在打，不當成你接手；"
                                    + $"下一次先原地等戰鬥結束，脫離戰鬥 {HuntCombatWait.SettleMs / 1000f:F1} 秒後再施放。");
            }

            // 被中止（停止自動化）不算接手：那時整條都在收。
            if (ending != "逾時" || !seen.PlayerTookOver(mountedAtCast))
            {
                return;
            }

            var fresh = Takeover.Note(now);
            Svc.Log.Information($"[BOCCHI][返回讓位] 第 {number} 次返回被你的操作打斷（{seen.Cause()}）：先不再施放；你停下（沒騎坐騎、靜止 {ReturnTakeover.QuietMs / 1000f:F1} 秒）就接著返回，"
                                + $"騎著坐騎或 {ReturnTakeover.WaitLimitMs / 1000} 秒內沒停下就這次不返回。");
            if (fresh)
            {
                Svc.Chat.Print(I18N.T("modules.teleporter.messages.return_yield", new Dictionary<string, string>
                {
                    ["quiet"] = (ReturnTakeover.QuietMs / 1000f).ToString("0.#", CultureInfo.InvariantCulture),
                    ["limit"] = (ReturnTakeover.WaitLimitMs / 1000).ToString(CultureInfo.InvariantCulture),
                }));
            }
        }

        // 施放返回之後每一拍呼叫（讀圖中不呼叫）：記下移動、vnavmesh、坐騎、戰鬥、別的詠唱；vnavmesh 的路線是誰的只在第一次看到它在走時問。
        private void Observe(Vector3 position, ReturnCastKind kind)
        {
            var moved = 0f;
            if (castFromKnown)
            {
                var dx = position.X - castFrom.X;
                var dz = position.Z - castFrom.Z;
                moved = MathF.Sqrt(dx * dx + dz * dz);
            }

            var running = QueryVnavRunning();
            var owner = seen.NeedsOwner(running) ? JudgeVnavOwner() : OwnPathVerdict.Idle;
            var condition = Svc.Condition;
            seen.Observe(moved, running, owner,
                condition[ConditionFlag.Mounted] || condition[ConditionFlag.MountOrOrnamentTransition] || kind == ReturnCastKind.Mount,
                condition[ConditionFlag.InCombat], kind == ReturnCastKind.Other, condition[ConditionFlag.Jumping] || condition[ConditionFlag.Jumping61]);
        }

        private bool QueryVnavRunning()
        {
            try
            {
                return vnav != null && vnav.IsRunning();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool QueryVnavComputing()
        {
            try
            {
                return vnav != null && vnav.IsSimpleMoveInProgress();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>施放前一拍：停下 BOCCHI 自己的導航（含還在算的尋路），有停到東西才寫記錄。</summary>
        public void StopNavigationBeforeCast()
        {
            var stopped = StopOwnNavigation();
            if (stopped.Length > 0)
            {
                Svc.Log.Information($"[BOCCHI][返回] 第 {number} 次嘗試：施放返回前 vnavmesh 還在走或還在算路：{stopped}角色位置 {Player.Position:F1}");
            }
        }

        private OwnPathVerdict JudgeVnavOwner()
        {
            try
            {
                var waypoints = vnav!.GetWaypoints();
                own.CaptureCurrent(TravelIntent.State);
                return own.Judge(true, waypoints is { Count: > 0 } ? waypoints[^1] : null);
            }
            catch (Exception)
            {
                return OwnPathVerdict.Unknown;
            }
        }

        private void Enter(ReturnStage next, long now)
        {
            stage = next;
            stageAt = now;
            OwnCastInFlight = next is ReturnStage.WaitCastStart or ReturnStage.Casting;
        }

        private static bool IsLoading()
        {
            var condition = Svc.Condition;
            return condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51];
        }

        private static long Limit(ReturnStage stage)
        {
            return stage switch
            {
                ReturnStage.Dismount => DismountTimeLimitMs,
                ReturnStage.WaitCastStart => CastStartTimeLimitMs,
                ReturnStage.Casting => CastEndTimeLimitMs,
                ReturnStage.WaitLoadingStart => LoadingTimeLimitMs,
                _ => 0,
            };
        }

        private static string YesNo(bool value)
        {
            return value ? "是" : "否";
        }
    }
}

/// <summary>
/// 返回施放之後看到的、可能打斷返回的事（[返回失敗] 的「原因」欄）。純邏輯、不碰遊戲（離線驗收直接呼叫）；只在框架執行緒用。
/// 「角色移動」只在 vnavmesh 沒在走時才算（多半是手動操作或擊退；讀不到按鍵輸入）。
/// </summary>
public sealed class ReturnInterruptions
{
    /// <summary>離施放位置（只看 X/Z）超過這個距離才算有移動。</summary>
    public const float MovedThresholdM = 0.5f;

    /// <summary>施放之後至少看過一拍；false＝還沒施放就失敗（例：下坐騎、等冷卻），各欄位不適用。</summary>
    public bool Observed { get; private set; }

    public float MovedMeters { get; private set; }

    public bool VnavMoving { get; private set; }

    /// <summary>第一次看到 vnavmesh 在走時那條路線是誰的；沒看到它在走是 <see cref="OwnPathVerdict.Idle"/>。</summary>
    public OwnPathVerdict VnavOwner { get; private set; } = OwnPathVerdict.Idle;

    public bool Mount { get; private set; }

    public bool Combat { get; private set; }

    public bool OtherCast { get; private set; }

    public bool Jump { get; private set; }

    /// <summary>這一拍要不要問路線是誰的：vnavmesh 在走、而且還沒問過。</summary>
    public bool NeedsOwner(bool vnavRunning)
    {
        return vnavRunning && VnavOwner == OwnPathVerdict.Idle;
    }

    /// <param name="moved">離施放位置的水平距離。</param>
    /// <param name="owner">只在 <see cref="NeedsOwner"/> 為 true 的那一拍有意義。</param>
    public void Observe(float moved, bool vnavRunning, OwnPathVerdict owner, bool mount, bool combat, bool otherCast, bool jump)
    {
        Observed = true;
        MovedMeters = MathF.Max(MovedMeters, moved);
        if (NeedsOwner(vnavRunning))
        {
            VnavOwner = owner == OwnPathVerdict.Idle ? OwnPathVerdict.Unknown : owner;
        }

        VnavMoving |= vnavRunning;
        Mount |= mount;
        Combat |= combat;
        OtherCast |= otherCast;
        Jump |= jump;
    }

    /// <returns>看到的事以「、」串起來；什麼都沒看到是「無」；還沒施放是「-」。</returns>
    public string Cause()
    {
        if (!Observed)
        {
            return "-";
        }

        var parts = new System.Collections.Generic.List<string>(4);
        if (VnavMoving)
        {
            parts.Add($"vnav移動({OwnerLabel(VnavOwner)})");
        }
        else if (MovedMeters >= MovedThresholdM)
        {
            parts.Add("角色移動");
        }

        if (Mount)
        {
            parts.Add("坐騎");
        }

        if (Combat)
        {
            parts.Add("戰鬥");
        }

        if (OtherCast)
        {
            parts.Add("其他詠唱");
        }

        if (Jump)
        {
            parts.Add("跳躍");
        }

        return parts.Count == 0 ? "無" : string.Join("、", parts);
    }

    public string MovedLabel()
    {
        return Observed ? MovedMeters.ToString("F1", CultureInfo.InvariantCulture) : "-";
    }

    /// <returns>否／BOCCHI／別人／?（讀不到路線）；還沒施放是「-」。</returns>
    public string VnavLabel()
    {
        if (!Observed)
        {
            return "-";
        }

        return VnavMoving ? OwnerLabel(VnavOwner) : "否";
    }

    /// <summary>
    /// 施放後玩家自己接手：vnavmesh 沒在走而角色移動了，或施放時沒騎乘、之後卻上了坐騎。
    /// 擊退也會被算進前者，代價只是下一次施放前多等玩家靜止約 1.5 秒（<see cref="ReturnTakeover"/>）。
    /// 看過戰鬥就不算：戰鬥中的移動多半是戰鬥 AI 在打，下一次嘗試改成原地等戰鬥結束。
    /// </summary>
    public bool PlayerTookOver(bool mountedAtCast)
    {
        return Observed && !Combat && ((!VnavMoving && MovedMeters >= MovedThresholdM) || (Mount && !mountedAtCast));
    }

    private static string OwnerLabel(OwnPathVerdict owner)
    {
        return owner switch
        {
            OwnPathVerdict.Own => "BOCCHI",
            OwnPathVerdict.Foreign => "別人",
            _ => "?",
        };
    }
}
