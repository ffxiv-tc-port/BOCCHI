using System;
using BOCCHI.ActionHelpers;
using BOCCHI.Chains;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.PhantomJobs;
using Dalamud.Game.ClientState.Conditions;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using Ocelot.Chain;

namespace BOCCHI.Modules.Treasure;

/// <summary>
/// 寶箱巡迴開始／續跑、規劃之前：沒有能用的魔尋寶感知數就先換成輔助自由人放一次、等結果、換回原本的輔助職業。
/// 每一段（開始或續跑）最多一次；不能放就照常巡全部生成點，不擋巡迴。只在框架執行緒用。
/// 魔尋寶還在冷卻就等它轉完再放（有上限）；等的時候不補放的條件成立就不放。
/// 刻意不在中止時換回原職：中止可能來自換區、登出，那時不碰遊戲。
/// </summary>
public sealed class HuntSenseCast(TreasureModule module)
{
    private const string Tag = "[BOCCHI][寶箱巡迴] 開始前補放魔尋寶：";

    private bool pending;

    private bool active;

    // 動作鏈送出到收尾（報告或中止）之間；巡迴停下（Disarm）不清，中止時才寫得出那一行。
    private bool chainLive;

    private long submittedAt;

    private Job? startingJob;

    private HuntSenseCastSkip result;

    private long stageAt;

    private long lastTryAt;

    private int dismountTries;

    private bool sent;

    private long castAt;

    private int sequenceAtCast;

    private bool senseTimedOut;

    private bool switchSent;

    // 第一次看到冷卻的時刻（0＝沒等過）、等了多久、放棄時讀到的剩餘冷卻。
    private long coolAt;

    private long cooledMs;

    private float coolSeen;

    /// <summary>開始或續跑時呼叫：這一段規劃前要看一次。</summary>
    public void Arm()
    {
        pending = true;
        active = false;
    }

    /// <summary>巡迴停下時呼叫；已送出的動作鏈由停止流程收。</summary>
    public void Disarm()
    {
        pending = false;
        active = false;
    }

    /// <returns>true＝這一拍先不規劃（補放中）。</returns>
    public bool Hold()
    {
        if (active)
        {
            if (!HuntSenseCastRules.HoldExpired(Environment.TickCount64 - submittedAt))
            {
                return true;
            }

            active = false;
            Svc.Log.Information($"{Tag}送出後 {HuntSenseCastRules.HoldLimitMs / 1000} 秒還沒收尾（動作鏈可能被收掉了），不再等，照常規劃");
            return false;
        }

        if (!pending)
        {
            return false;
        }

        pending = false;
        var skip = Decide(out var job, out var level);
        if (skip == HuntSenseCastSkip.Off)
        {
            return false;
        }

        if (skip != HuntSenseCastSkip.None)
        {
            Svc.Log.Information(Tag + SkipText(skip, level));
            return false;
        }

        Begin(job!);
        return true;
    }

    private HuntSenseCastSkip Decide(out Job? job, out int level)
    {
        var config = module.Config;
        var enabled = config.HuntSkipBySenseCount && config.HuntCastTreasureSightIfUnknown;
        job = null;
        level = -1;
        var pre = HuntSenseCastRules.Precheck(enabled, module.SenseCount.Known, module.HuntMemory.Participating);
        if (pre != HuntSenseCastSkip.None)
        {
            return pre;
        }

        job = Job.Current;
        Span<byte> levels = stackalloc byte[PhantomJobTable.Count];
        if (job != null && PhantomJobTable.ReadLevels(levels) >= 0)
        {
            level = levels[(int)JobId.Freelancer];
        }

        var player = Svc.Objects.LocalPlayer;
        return HuntSenseCastRules.Decide(enabled, module.SenseCount.Known, module.HuntMemory.Participating,
            job != null, level, Svc.Condition[ConditionFlag.InCombat], player?.IsCasting == true);
    }

    private string SkipText(HuntSenseCastSkip skip, int level)
    {
        return skip switch
        {
            HuntSenseCastSkip.Known => $"不用補放，已有感知數（{module.SenseProgressText()}，感知後 {(Environment.TickCount64 - module.SenseCount.SensedAt) / 1000} 秒）",
            HuntSenseCastSkip.Participating => "不補放（正在參與 F.A.T.E.／緊急遭遇戰，這時的數字不採用），照常巡全部生成點",
            HuntSenseCastSkip.JobUnknown => "不補放（讀不到目前的輔助職業，換了換不回來），照常巡全部生成點",
            HuntSenseCastSkip.LevelUnknown => "不補放（讀不到輔助自由人的等級），照常巡全部生成點",
            HuntSenseCastSkip.NotLearned => $"不補放（輔助自由人 Lv{level}，{TreasureSightRules.LearnLevel} 級才學會魔尋寶），照常巡全部生成點",
            HuntSenseCastSkip.InCombat => "不補放（戰鬥中），照常巡全部生成點",
            HuntSenseCastSkip.Casting => "不補放（還在詠唱），照常巡全部生成點",
            _ => skip.ToString(),
        };
    }

    private void Begin(Job job)
    {
        active = true;
        chainLive = true;
        submittedAt = Environment.TickCount64;
        startingJob = job;
        result = HuntSenseCastSkip.None;
        stageAt = 0;
        lastTryAt = 0;
        dismountTries = 0;
        sent = false;
        castAt = 0;
        senseTimedOut = false;
        switchSent = false;
        coolAt = 0;
        cooledMs = 0;
        coolSeen = 0f;
        Svc.Log.Debug($"{Tag}開始（目前輔助職業 {PhantomJobTable.GetName((int)job.id)}{(Svc.Condition[ConditionFlag.Mounted] ? "，騎乘中先下坐騎" : string.Empty)}）");
        Plugin.Chain.Submit(() => Chain.Create("TreasureHunt.SenseCast")
            .Then(new TaskManagerTask(Dismount, "SenseCast.Dismount", new TaskManagerConfiguration { TimeLimitMS = 10_000 }))
            .ConditionalThen(_ => result == HuntSenseCastSkip.None, SwitchToFreelancer)
            .Then(new TaskManagerTask(Cast, "SenseCast.Cast", new TaskManagerConfiguration { TimeLimitMS = HuntSenseCastRules.CastStageLimitMs }))
            .Then(new TaskManagerTask(WaitSense, "SenseCast.WaitSense", new TaskManagerConfiguration { TimeLimitMS = 20_000 }))
            .ConditionalThen(_ => switchSent && startingJob != null && startingJob.id != JobId.Freelancer, () => startingJob!.ChangeToChain())
            .Then(new TaskManagerTask(WaitApplied, "SenseCast.WaitApplied", new TaskManagerConfiguration { TimeLimitMS = 5_000 }))
            .Then(_ => Report())
            .OnCancel(ReportCancelled));
    }

    private Chain SwitchToFreelancer()
    {
        switchSent = true;
        return Job.Freelancer.ChangeToChain();
    }

    private bool Dismount()
    {
        var now = Environment.TickCount64;
        if (stageAt == 0)
        {
            stageAt = now;
        }

        var condition = Svc.Condition;
        var player = Svc.Objects.LocalPlayer;
        var castingMount = player != null && ReturnCast.Classify(player.IsCasting, player.CastActionType, player.CastActionId) == ReturnCastKind.Mount;
        var done = ReturnCast.DismountStep(condition[ConditionFlag.Mounted], condition[ConditionFlag.MountOrOrnamentTransition], castingMount, now - stageAt, now - lastTryAt,
            out var tryNow, out var gaveUp);
        if (tryNow)
        {
            // 同返回動作鏈：兩種下坐騎的寫法輪流送。
            if (dismountTries++ % 2 == 0)
            {
                Actions.TryUnmount();
            }
            else
            {
                Actions.Dismount.Cast();
            }

            lastTryAt = now;
        }

        if (done)
        {
            stageAt = 0;
            lastTryAt = 0;
            if (gaveUp)
            {
                result = HuntSenseCastSkip.Mounted;
            }
        }

        return done;
    }

    private bool Cast()
    {
        if (result != HuntSenseCastSkip.None)
        {
            return true;
        }

        var now = Environment.TickCount64;
        if (stageAt == 0)
        {
            stageAt = now;
        }

        var onFreelancer = Job.Current?.id == JobId.Freelancer;
        var recast = onFreelancer ? Actions.Freelancer.Treasuresight.GetRecastTime() : 0f;
        if (onFreelancer && !sent)
        {
            var gate = CooldownGate(now, recast);
            if (gate != HuntSenseCooldown.Ready)
            {
                return gate == HuntSenseCooldown.GiveUp;
            }

            // 送出的上限從可以送的這一拍起算：等冷卻的時間不算。
            stageAt = now;
        }

        var step = HuntSenseCastRules.CastStep(now - stageAt, onFreelancer, sent, now - lastTryAt, recast, out var tryNow);
        if (tryNow)
        {
            if (!sent)
            {
                sequenceAtCast = TreasureSenseNotice.Sequence;
            }

            Actions.Freelancer.Treasuresight.Cast();
            sent = true;
            lastTryAt = now;
            castAt = now;
        }

        switch (step)
        {
            case HuntSenseCastStep.Sent:
                return true;
            case HuntSenseCastStep.Cooldown:
                result = HuntSenseCastSkip.Cooldown;
                return true;
            case HuntSenseCastStep.NotCast:
                result = HuntSenseCastSkip.NotCast;
                return true;
            case HuntSenseCastStep.NotSwitched:
                result = HuntSenseCastSkip.NotSwitched;
                return true;
            default:
                return false;
        }
    }

    private HuntSenseCooldown CooldownGate(long now, float recast)
    {
        var waited = coolAt == 0 ? 0 : now - coolAt;
        var gate = HuntSenseCastRules.CooldownStep(recast, waited, module.SenseCount.Known, module.HuntMemory.Participating,
            Svc.Condition[ConditionFlag.InCombat], Svc.Objects.LocalPlayer?.IsCasting == true, out var skip);
        cooledMs = waited;
        if (gate == HuntSenseCooldown.GiveUp)
        {
            coolSeen = recast;
            result = skip;
        }
        else if (gate == HuntSenseCooldown.Cooling && coolAt == 0)
        {
            coolAt = now;
            Svc.Log.Debug($"{Tag}魔尋寶還在冷卻（剩 {recast:F1} 秒），等它轉完再放（最多等 {HuntSenseCastRules.CooldownWaitMaxMs / 1000} 秒）");
        }

        return gate;
    }

    private bool WaitSense()
    {
        if (result != HuntSenseCastSkip.None)
        {
            return true;
        }

        var done = HuntSenseCastRules.SenseWaitDone(sequenceAtCast, TreasureSenseNotice.Sequence, Environment.TickCount64 - castAt, out var timedOut);
        senseTimedOut = timedOut;
        return done;
    }

    // 模組每幀才取用聊天裡的結果：等它取用了再報。
    private bool WaitApplied()
    {
        return result != HuntSenseCastSkip.None || senseTimedOut || module.ConsumedSenseSequence == TreasureSenseNotice.Sequence
               || Environment.TickCount64 - castAt >= 1000 + HuntSenseCastRules.SenseWaitMs;
    }

    private void Report()
    {
        var elapsed = (Environment.TickCount64 - submittedAt) / 1000f;
        var back = string.Empty;
        if (switchSent && startingJob != null && startingJob.id != JobId.Freelancer)
        {
            var now = Job.Current;
            back = now?.id == startingJob.id
                ? $"，已換回{PhantomJobTable.GetName((int)startingJob.id)}"
                : $"，沒換回{PhantomJobTable.GetName((int)startingJob.id)}（目前 {(now == null ? "讀不到" : PhantomJobTable.GetName((int)now.id))}）";
        }

        string line;
        if (result != HuntSenseCastSkip.None)
        {
            line = result switch
            {
                HuntSenseCastSkip.Mounted => $"不補放（{ReturnCast.DismountGiveUpMs / 1000} 秒內沒下坐騎）",
                HuntSenseCastSkip.Cooldown => $"不補放（魔尋寶的冷卻等了 {HuntSenseCastRules.CooldownWaitMaxMs / 1000} 秒還沒轉完）",
                HuntSenseCastSkip.CooldownUnknown => float.IsNaN(coolSeen)
                    ? "不補放（讀不到魔尋寶剩下的冷卻，不等）"
                    : $"不補放（魔尋寶剩下的冷卻讀到 {coolSeen:F1} 秒，超過最多等的 {HuntSenseCastRules.CooldownWaitMaxMs / 1000} 秒，不等）",
                HuntSenseCastSkip.Known => $"不用補放（等魔尋寶冷卻時已經拿到感知數：{module.SenseProgressText()}）",
                HuntSenseCastSkip.Participating => "不補放（等魔尋寶冷卻時開始參與 F.A.T.E.／緊急遭遇戰，這時的數字不採用）",
                HuntSenseCastSkip.InCombat => "不補放（等魔尋寶冷卻時進入戰鬥）",
                HuntSenseCastSkip.Casting => "不補放（等魔尋寶冷卻時開始詠唱）",
                HuntSenseCastSkip.NotSwitched => $"不補放（{HuntSenseCastRules.CastGiveUpMs / 1000} 秒內沒換成輔助自由人，不送施放）",
                _ => $"送出施放後遊戲沒有開始冷卻（沒放出去，{HuntSenseCastRules.CastGiveUpMs / 1000} 秒內試了數次）",
            };
            line += result == HuntSenseCastSkip.Known ? "，照這個數字規劃" : "，照常巡全部生成點";
        }
        else if (senseTimedOut)
        {
            line = $"已施放但 {HuntSenseCastRules.SenseWaitMs / 1000} 秒內沒收到結果，照常巡全部生成點";
        }
        else if (module.SenseCount.Known)
        {
            line = $"拿到感知數 銀 {module.SenseCount.SensedSilver}、銅 {module.SenseCount.SensedBronze}，照這個數字規劃";
        }
        else
        {
            line = "收到結果但這次的數字不用（原因見上一行「魔尋寶感知」），照常巡全部生成點";
        }

        active = false;
        chainLive = false;
        Svc.Log.Information($"{Tag}{line}（耗時 {elapsed:F1} 秒{CooledText()}{back}）");
    }

    private string CooledText()
    {
        return coolAt == 0 ? string.Empty : $"，其中等魔尋寶冷卻 {cooledMs / 1000f:F1} 秒";
    }

    // 停止巡迴、全部停止（含 BOCCHI.StopAll）或動作鏈逾時都走這裡。
    private void ReportCancelled()
    {
        if (!chainLive)
        {
            return;
        }

        chainLive = false;
        active = false;
        Svc.Log.Information($"{Tag}中止（巡迴停止或動作鏈被收掉，耗時 {(Environment.TickCount64 - submittedAt) / 1000f:F1} 秒{CooledText()}）"
                            + (switchSent ? "；已送出換成輔助自由人，不再自動換回" : string.Empty));
    }
}
