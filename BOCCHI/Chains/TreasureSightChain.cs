using System;
using BOCCHI.ActionHelpers;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.PhantomJobs;
using BOCCHI.Modules.Treasure;
using ECommons.DalamudServices;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;

namespace BOCCHI.Chains;

public class TreasureSightChain(TreasureModule module) : ChainFactory
{
    private readonly Job? StartingJob = Job.Current;

    // 上一次確認放出去的時刻與上一次寫 Information 的略過原因；只記在記憶體（重新載入插件後第一次返回照放）。只在框架執行緒讀寫。
    private static long? lastCastAt;

    private static TreasureSightSkip lastReported = TreasureSightSkip.None;

    private static long lastReportedAt;

    protected override Chain Create(Chain chain)
    {
        chain.RunIf(() => module.Config.CastTreasureSightUponReturn);
        // 不知道目前的輔助職業就不換職：換了之後換不回來。
        chain.BreakIf(() => StartingJob == null);
        chain.BreakIf(() => ShouldSkip());

        chain.Then(Job.Freelancer.ChangeToChain);
        // 換職那一步逾時也會走到這裡：沒換成輔助自由人就不送（同一個動作格在別的輔助職業是別的技能）；換回原職照做。
        chain.ConditionalThen(_ => OnFreelancer(), () => Actions.Freelancer.Treasuresight.CastOnChain(Chain.Create("TreasureSight.Cast"))
            .Wait(1000)
            .Then(_ => ConfirmCast()));
        chain.Then(() => StartingJob!.ChangeToChain());

        return chain;
    }

    // 還沒學會（或讀不到等級）、或還在重新探測間隔內：不換職、這次不施放。
    private bool ShouldSkip()
    {
        Span<byte> levels = stackalloc byte[PhantomJobTable.Count];
        var level = PhantomJobTable.ReadLevels(levels) < 0 ? -1 : levels[(int)JobId.Freelancer];
        var now = Environment.TickCount64;
        var interval = module.Config.TreasureSightIntervalMinutes;
        var skip = TreasureSightRules.Decide(level, lastCastAt, now, interval, out var remainingMs);
        if (skip == TreasureSightSkip.None)
        {
            return false;
        }

        var line = skip switch
        {
            TreasureSightSkip.LevelUnknown => "[BOCCHI][魔尋寶] 讀不到輔助自由人的等級：當作還沒學會魔尋寶，不換職、這次返回不施放。",
            TreasureSightSkip.NotLearned => $"[BOCCHI][魔尋寶] 輔助自由人 Lv{level}，{TreasureSightRules.LearnLevel} 級才學會魔尋寶：不換職、這次返回不施放。",
            _ => $"[BOCCHI][魔尋寶] 距上次施放魔尋寶未滿 {interval} 分鐘（還有約 {Math.Ceiling(remainingMs / 60000.0):F0} 分鐘）：寶箱要打怪或參加 F.A.T.E.／緊急遭遇戰後才會刷新，不換職、這次返回不施放。",
        };
        if (TreasureSightRules.ShouldReport(skip, lastReported, lastReportedAt, now))
        {
            lastReported = skip;
            lastReportedAt = now;
            Svc.Log.Information(line);
        }
        else
        {
            Svc.Log.Debug(line);
        }

        return true;
    }

    private static bool OnFreelancer()
    {
        if (Job.Current?.id == JobId.Freelancer)
        {
            return true;
        }

        Svc.Log.Information("[BOCCHI][魔尋寶] 沒換成輔助自由人（換職沒有生效或逾時）：不送施放，這次返回不施放。");
        return false;
    }

    // 送出之後遊戲開始冷卻＝真的放出去了；沒開始冷卻（沒放出去）不記，下次返回再試。
    private void ConfirmCast()
    {
        var recast = Actions.Freelancer.Treasuresight.GetRecastTime();
        if (recast <= 0f)
        {
            Svc.Log.Information("[BOCCHI][魔尋寶] 送出施放後遊戲沒有開始冷卻：當作沒放出去，下次返回再試。");
            return;
        }

        lastCastAt = Environment.TickCount64;
        lastReported = TreasureSightSkip.None;
        Svc.Log.Information($"[BOCCHI][魔尋寶] 已施放（冷卻還有 {recast:F1} 秒）：之後 {module.Config.TreasureSightIntervalMinutes} 分鐘內返回不再換職施放（0＝每次都放）。");
    }
}
