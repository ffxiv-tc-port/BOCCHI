using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.ItemHelpers;
using BOCCHI.Modules.Automator;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.Exp;
using BOCCHI.Modules.Fates;
using BOCCHI.Modules.StateManager;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace BOCCHI.Ledger;

/// <summary>
/// 新月島的場次帳（[場次]）、活動帳（[活動帳]、[選活動]）與待命記錄（[待命]），全部寫 Information，給場次彙總工具解析。
/// 契約：只在 framework 執行緒（Framework.Update、聊天、換區、登出、非法模式的呼叫）；只存數值、ID 與受管物件，不存原生指標。
/// 每幀只做比較；背包每 <see cref="PollIntervalMs"/> 讀一次、讀取畫面中不讀；計數不跟著介面上的重置歸零。
/// </summary>
public sealed class IslandLedger : IDisposable
{
    private const long PollIntervalMs = 250;

    private const long KnowledgePollIntervalMs = 1000;

    // 輪詢之間隔太久（讀取畫面）不全算進非法模式時間。
    private const long MaxIllegalStepMs = 60_000;

    private const int MaxCandidates = 5;

    private const int MaxKnowledgeShapes = 4;

    private const float CampRadius = 40f;

    private readonly AutomatorModule? automator;

    private readonly StateManagerModule? states;

    private readonly FatesModule? fates;

    private readonly CriticalEncountersModule? encounters;

    private readonly Regex? expRegex;

    private readonly string? expProbe;

    private readonly bool knowledgeTemplate;

    private readonly string version;

    private bool disposed;

    // ---- 場次
    private bool active;

    private bool baselineReady;

    private DateTime enteredLocal;

    private string enteredHow = "進島";

    private long startedAt;

    private long nextPollAt;

    private long nextKnowledgeAt;

    private long lastPollAt;

    private int lastSilver;

    private int lastGold;

    private long silverGained;

    private long goldGained;

    private long expGained;

    private long knowledgeGained;

    private long knowledgeLost;

    private int knowledgeStart;

    private int knowledgeLast;

    private long illegalMs;

    private bool illegalWasOn;

    private int deaths;

    private bool wasDead;

    private int fatesSelected;

    private int criticalEncountersSelected;

    private readonly HashSet<ulong> fatesJoined = [];

    private readonly HashSet<ulong> criticalEncountersJoined = [];

    private bool knowledgeMatchedOnce;

    private readonly HashSet<string> knowledgeShapes = [];

    private readonly HashSet<string> failedAt = [];

    // ---- 活動
    private ActivityEntry? entry;

    // ---- 待命
    private bool idle;

    private long idleSince;

    private int idleReturns;

    public IslandLedger(Plugin plugin)
    {
        plugin.Modules.TryGetModule(out automator);
        plugin.Modules.TryGetModule(out states);
        plugin.Modules.TryGetModule(out fates);
        plugin.Modules.TryGetModule(out encounters);
        version = plugin.Version;

        var language = Svc.ClientState.ClientLanguage;
        var pattern = ExpTracker.GetExpMessagePattern(language);
        expRegex = pattern == null ? null : new Regex(pattern, RegexOptions.Compiled);
        knowledgeTemplate = language is (ClientLanguage)5 or (ClientLanguage)7;
        expProbe = knowledgeTemplate ? ExpMessagePatterns.TraditionalChineseProbe : null;

        if (automator != null)
        {
            automator.automator.Ledger = this;
        }

        if (states != null)
        {
            states.OnEnterInFate += OnEnterFate;
            states.OnEnterInCriticalEncounter += OnEnterCriticalEncounter;
        }

        Svc.Framework.Update += Tick;
        Svc.Chat.ChatMessage += OnChat;
        Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
        Svc.ClientState.Logout += OnLogout;

        if (Svc.ClientState.TerritoryType == ZoneData.SOUTHHORN)
        {
            Begin("載入時已在島上");
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Svc.Framework.Update -= Tick;
        Svc.Chat.ChatMessage -= OnChat;
        Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Svc.ClientState.Logout -= OnLogout;
        if (states != null)
        {
            states.OnEnterInFate -= OnEnterFate;
            states.OnEnterInCriticalEncounter -= OnEnterCriticalEncounter;
        }

        try
        {
            if (entry != null)
            {
                if (!entry.Ended)
                {
                    EndEntry(ActivityEnd.Unloaded, Environment.TickCount64);
                }
                else
                {
                    Settle("立即");
                }
            }

            if (active)
            {
                End("卸載（場次未結束）", ActivityEnd.Unloaded);
            }
        }
        catch (Exception ex)
        {
            Fail("Dispose", ex);
        }

        if (automator != null && automator.automator.Ledger == this)
        {
            automator.automator.Ledger = null;
        }
    }

    // ================================================================ 非法模式呼叫的介面

    /// <summary>非法模式定了一個活動（含接手、改去 CE）。前一個還沒結算的這時結算。</summary>
    public void Selected(Activity activity, ActivitySource source)
    {
        try
        {
            SelectedCore(activity, source);
        }
        catch (Exception ex)
        {
            Fail("Selected", ex);
        }
    }

    private void SelectedCore(Activity activity, ActivitySource source)
    {
        var now = Environment.TickCount64;
        PollCurrency(now);
        if (entry != null)
        {
            if (!entry.Ended)
            {
                EndEntry(source switch
                {
                    ActivitySource.SwitchedToCriticalEncounter => ActivityEnd.SwitchedToCriticalEncounter,
                    ActivitySource.SwitchedToPot => ActivityEnd.SwitchedToPot,
                    _ => ActivityEnd.Vanished,
                }, now);
            }

            if (entry != null)
            {
                Settle("下一個活動");
            }
        }

        var name = activity.GetName();
        var kind = activity.KindLabel;
        if (idle)
        {
            EndIdle(now, $"{kind}#{activity.data.Id}", name);
        }

        if (active && source != ActivitySource.Resumed)
        {
            if (activity is FateActivity)
            {
                fatesSelected++;
            }
            else
            {
                criticalEncountersSelected++;
            }
        }

        var fate = activity is FateActivity && fates != null && fates.fates.TryGetValue(activity.data.Id, out var tracked) ? tracked : null;
        entry = new ActivityEntry(activity, kind, name, source)
        {
            ProgressAtSelect = SafeProgress(activity),
            EtaAtSelect = fate == null ? "-" : Eta(fate),
            ReturnFailuresAtSelect = BOCCHI.Chains.ReturnChain.FailureCount,
            LastState = activity.state,
        };
        entry.Window.Open(now);
        LogCandidates(kind, activity.data.Id, name, source);
    }

    /// <summary>每幀（非法模式在跑時）呼叫：只比較狀態，轉換時記到場／開打時間。</summary>
    public void Observe(Activity? activity)
    {
        try
        {
            ObserveCore(activity);
        }
        catch (Exception ex)
        {
            Fail("Observe", ex);
        }
    }

    private void ObserveCore(Activity? activity)
    {
        if (entry is not { Ended: false } current || activity == null || !ReferenceEquals(current.Activity, activity) || activity.state == current.LastState)
        {
            return;
        }

        current.LastState = activity.state;
        var now = Environment.TickCount64;
        switch (activity.state)
        {
            case ActivityState.WaitingToStartCriticalEncounter:
                activity.Record.MarkArrived(now, SafeProgress(activity));
                break;
            case ActivityState.Participating:
                activity.Record.MarkParticipating(now, SafeProgress(activity));
                break;
        }
    }

    public void Ended(Activity activity, ActivityEnd end)
    {
        try
        {
            EndedCore(activity, end);
        }
        catch (Exception ex)
        {
            Fail("Ended", ex);
        }
    }

    private void EndedCore(Activity activity, ActivityEnd end)
    {
        if (entry is not { Ended: false } current || !ReferenceEquals(current.Activity, activity))
        {
            return;
        }

        EndEntry(end, Environment.TickCount64);
    }

    /// <summary>非法模式找不到活動（每幀呼叫，只有第一次寫記錄）。</summary>
    public void IdleTick()
    {
        try
        {
            IdleTickCore();
        }
        catch (Exception ex)
        {
            Fail("IdleTick", ex);
        }
    }

    private void IdleTickCore()
    {
        if (idle)
        {
            return;
        }

        idle = true;
        idleSince = Environment.TickCount64;
        idleReturns = 0;
        var position = Svc.Objects.LocalPlayer?.Position;
        var shard = position is { } p ? AethernetData.GetClosestTo(p) : null;
        Svc.Log.Information(string.Create(CultureInfo.InvariantCulture,
            $"[BOCCHI][待命] 開始 位置={Format(position)} 最近乙太之晶=「{(shard == null ? "?" : shard.Aethernet.ToFriendlyString())}」 距離={(shard == null || position == null ? "?" : Vector3.Distance(shard.Position, position.Value).ToString("F1", CultureInfo.InvariantCulture))} 離營地={CampDistance(position)}"));
    }

    public void IdleReturnSubmitted()
    {
        if (idle)
        {
            idleReturns++;
        }
    }

    /// <summary>非法模式停止或換區時呼叫（Automator.Refresh）。</summary>
    public void Stopped(ActivityEnd end)
    {
        try
        {
            StoppedCore(end);
        }
        catch (Exception ex)
        {
            Fail("Stopped", ex);
        }
    }

    private void StoppedCore(ActivityEnd end)
    {
        if (idle)
        {
            EndIdle(Environment.TickCount64, $"停止({LedgerRules.Label(end)})", "-");
        }
    }

    // ================================================================ 每幀與事件

    private void Tick(IFramework framework)
    {
        try
        {
            TickCore();
        }
        catch (Exception ex)
        {
            Fail("Tick", ex);
        }
    }

    private void TickCore()
    {
        var now = Environment.TickCount64;
        if (entry is { Ended: true } pending && pending.Window.IsDue(now))
        {
            PollCurrency(now);
            Settle("滿15秒");
        }

        if (!active || now < nextPollAt)
        {
            return;
        }

        nextPollAt = now + PollIntervalMs;
        Poll(now);
    }

    private void Poll(long now)
    {
        var condition = Svc.Condition;
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            return;
        }

        var player = Svc.Objects.LocalPlayer;
        if (player == null)
        {
            return;
        }

        var illegalOn = automator?.Config.Enabled == true;
        if (illegalOn && illegalWasOn && lastPollAt != 0)
        {
            illegalMs += Math.Min(now - lastPollAt, MaxIllegalStepMs);
        }

        illegalWasOn = illegalOn;
        lastPollAt = now;

        var dead = player.IsDead;
        if (dead && !wasDead)
        {
            deaths++;
        }

        wasDead = dead;

        if (now >= nextKnowledgeAt)
        {
            nextKnowledgeAt = now + KnowledgePollIntervalMs;
            var knowledge = KnowledgeLevel.Read();
            if (knowledge.IsAvailable)
            {
                if (knowledgeStart == 0)
                {
                    knowledgeStart = knowledge.Real;
                }

                knowledgeLast = knowledge.Real;
            }
        }

        PollCurrency(now);
    }

    private void PollCurrency(long now)
    {
        if (!active || Svc.Objects.LocalPlayer == null || Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51])
        {
            return;
        }

        var silver = Items.Silver.Count();
        var gold = Items.Gold.Count();
        if (!baselineReady)
        {
            baselineReady = true;
            lastSilver = silver;
            lastGold = gold;
            Svc.Log.Information(string.Create(CultureInfo.InvariantCulture,
                $"[BOCCHI][場次] 開始 進島={enteredLocal:HH:mm:ss} 來源={enteredHow} 版本={version} 知見Lv={Level(knowledgeStart)} 銀幣={silver} 金幣={gold}"));
            return;
        }

        if (silver > lastSilver)
        {
            AddIncome(IncomeKind.Silver, silver - lastSilver, now);
        }

        if (gold > lastGold)
        {
            AddIncome(IncomeKind.Gold, gold - lastGold, now);
        }

        lastSilver = silver;
        lastGold = gold;
    }

    private void OnChat(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        try
        {
            OnChatCore(type, message);
        }
        catch (Exception ex)
        {
            Fail("OnChat", ex);
        }
    }

    private void OnChatCore(XivChatType type, SeString message)
    {
        if (!active || IsPlayerChat(type))
        {
            return;
        }

        var text = message.TextValue;
        var now = Environment.TickCount64;
        if (expRegex != null && (expProbe == null || text.Contains(expProbe, StringComparison.Ordinal))
                             && ExpMessagePatterns.TryParseAmount(expRegex.Match(text), out var exp))
        {
            AddIncome(IncomeKind.PhantomExp, exp, now);
            return;
        }

        if (!knowledgeTemplate || !text.Contains("知見", StringComparison.Ordinal))
        {
            return;
        }

        if (LedgerRules.TryParseKnowledge(text, out var amount, out var lost))
        {
            AddIncome(lost ? IncomeKind.KnowledgeLost : IncomeKind.Knowledge, amount, now);
            if (!knowledgeMatchedOnce)
            {
                knowledgeMatchedOnce = true;
                Svc.Log.Information($"[BOCCHI][知見訊息] 首次命中（本次載入只記這一次）：{ChatPlayerMask.ForLog(message, out _)}");
            }

            return;
        }

        // 記錄裡不寫玩家名（玩家連結換成 <自己>／<玩家>）；提到別的玩家的（別人的知見等級變化等）與自己的收益無關，不算沒命中。
        var masked = ChatPlayerMask.ForLog(message, out var others);
        if (others > 0)
        {
            return;
        }

        if (knowledgeShapes.Count < MaxKnowledgeShapes && knowledgeShapes.Add(Regex.Replace(masked, @"\d+", "#")))
        {
            Svc.Log.Information($"[BOCCHI][知見訊息] 含「知見」但沒命中模板（形狀 {knowledgeShapes.Count}/{MaxKnowledgeShapes}）：{masked}");
        }
    }

    private void AddIncome(IncomeKind kind, long amount, long now)
    {
        if (amount <= 0)
        {
            return;
        }

        switch (kind)
        {
            case IncomeKind.Silver:
                silverGained += amount;
                break;
            case IncomeKind.Gold:
                goldGained += amount;
                break;
            case IncomeKind.PhantomExp:
                expGained += amount;
                break;
            case IncomeKind.Knowledge:
                knowledgeGained += amount;
                break;
            case IncomeKind.KnowledgeLost:
                knowledgeLost += amount;
                break;
        }

        entry?.Window.Add(kind, amount, now);
    }

    private unsafe void OnEnterFate(StateManagerModule _)
    {
        try
        {
            OnEnterFateCore();
        }
        catch (Exception ex)
        {
            Fail("OnEnterFate", ex);
        }
    }

    private unsafe void OnEnterFateCore()
    {
        if (!active)
        {
            return;
        }

        var manager = FateManager.Instance();
        if (manager == null || manager->CurrentFate == null)
        {
            return;
        }

        var fate = manager->CurrentFate;
        fatesJoined.Add(((ulong)fate->FateId << 32) | (uint)fate->StartTimeEpoch);
    }

    private unsafe void OnEnterCriticalEncounter(StateManagerModule _)
    {
        try
        {
            OnEnterCriticalEncounterCore();
        }
        catch (Exception ex)
        {
            Fail("OnEnterCriticalEncounter", ex);
        }
    }

    private unsafe void OnEnterCriticalEncounterCore()
    {
        if (!active)
        {
            return;
        }

        var container = DynamicEventContainer.GetInstance();
        if (container == null || container->CurrentEventId == 0)
        {
            return;
        }

        var id = container->CurrentEventId;
        var start = encounters != null && encounters.CriticalEncounters.TryGetValue(id, out var snapshot) ? snapshot.StartTimestamp : 0;
        criticalEncountersJoined.Add(((ulong)id << 32) | (uint)start);
    }

    private void OnTerritoryChanged(ushort territory)
    {
        try
        {
            OnTerritoryChangedCore(territory);
        }
        catch (Exception ex)
        {
            Fail("OnTerritoryChanged", ex);
        }
    }

    private void OnTerritoryChangedCore(ushort territory)
    {
        if (active)
        {
            End(territory == ZoneData.SOUTHHORN ? "換區" : "離島", ActivityEnd.ZoneChanged);
        }

        if (territory == ZoneData.SOUTHHORN)
        {
            Begin("進島");
        }
    }

    private void OnLogout(int type, int code)
    {
        try
        {
            OnLogoutCore();
        }
        catch (Exception ex)
        {
            Fail("OnLogout", ex);
        }
    }

    private void OnLogoutCore()
    {
        if (active)
        {
            End("登出", ActivityEnd.LoggedOut);
        }
    }

    // ================================================================ 場次

    private void Begin(string how)
    {
        active = true;
        baselineReady = false;
        enteredLocal = DateTime.Now;
        enteredHow = how;
        startedAt = Environment.TickCount64;
        nextPollAt = 0;
        nextKnowledgeAt = 0;
        lastPollAt = 0;
        silverGained = goldGained = expGained = knowledgeGained = knowledgeLost = 0;
        knowledgeStart = knowledgeLast = 0;
        illegalMs = 0;
        illegalWasOn = false;
        deaths = 0;
        wasDead = false;
        fatesSelected = criticalEncountersSelected = 0;
        fatesJoined.Clear();
        criticalEncountersJoined.Clear();
    }

    private void End(string reason, ActivityEnd activityEnd)
    {
        // 場次結束後的收益不屬於任何活動：還在等結算的這時結算。
        if (entry != null)
        {
            if (!entry.Ended)
            {
                EndEntry(activityEnd, Environment.TickCount64);
            }

            Settle("立即");
        }

        active = false;
        var now = Environment.TickCount64;
        var know = baselineReady;
        Svc.Log.Information(string.Create(CultureInfo.InvariantCulture,
            $"[BOCCHI][場次] 結束 原因={reason} 進島={enteredLocal:HH:mm:ss} 時長={LedgerRules.Minutes(now - startedAt)} 版本={version} 非法模式={LedgerRules.Minutes(illegalMs)} "
            + $"FATE={fatesJoined.Count}/{fatesSelected} CE={criticalEncountersJoined.Count}/{criticalEncountersSelected} 死亡={deaths} "
            + $"銀幣+={(know ? silverGained.ToString(CultureInfo.InvariantCulture) : "?")} 金幣+={(know ? goldGained.ToString(CultureInfo.InvariantCulture) : "?")} "
            + $"輔助經驗+={(expRegex == null ? "?" : expGained.ToString(CultureInfo.InvariantCulture))} 知見Lv={Level(knowledgeStart)}→{Level(knowledgeLast)} "
            + $"知見+={(knowledgeTemplate ? knowledgeGained.ToString(CultureInfo.InvariantCulture) : "?")} 知見-={(knowledgeTemplate ? knowledgeLost.ToString(CultureInfo.InvariantCulture) : "?")}"));
    }

    // ================================================================ 活動

    private void EndEntry(ActivityEnd end, long now)
    {
        if (entry == null || entry.Ended)
        {
            return;
        }

        entry.Ended = true;
        entry.End = end;
        entry.EndedAt = now;
        entry.ReturnFailuresAtEnd = BOCCHI.Chains.ReturnChain.FailureCount;
        entry.Window.End(now);
        entry.Activity = null;
        if (LedgerRules.SettlesImmediately(end))
        {
            Settle("立即");
        }
    }

    private void Settle(string how)
    {
        var current = entry;
        if (current == null)
        {
            return;
        }

        entry = null;
        var now = Environment.TickCount64;
        var record = current.Record;
        var window = current.Window;
        var endedAt = current.Ended ? current.EndedAt : now;
        var arrived = record.ArrivedAt;
        var joined = record.ParticipatingAt;
        var travel = LedgerRules.Span(record.SelectedAt, arrived != 0 ? arrived : endedAt);
        var wait = arrived == 0 ? -1 : LedgerRules.Span(arrived, joined != 0 ? joined : endedAt);
        var fight = joined == 0 ? -1 : LedgerRules.Span(joined, endedAt);
        var phase = joined != 0 ? "參加" : arrived != 0 ? "等開打" : "趕路";
        var arrival = window.LastIncomeAfterEnd() is { } ms ? LedgerRules.SignedSeconds(ms) : "?";
        window.Close();
        Svc.Log.Information(string.Create(CultureInfo.InvariantCulture,
            $"[BOCCHI][活動帳] kind={current.Kind} id={current.Id} name=「{current.Name}」 來源={LedgerRules.Label(current.Source)} 導航={record.Navigation ?? "-"} "
            + $"趕路={LedgerRules.Seconds(travel)} 等開打={LedgerRules.Seconds(wait)} 參加={LedgerRules.Seconds(fight)} 階段={phase} "
            + $"重來={Math.Max(0, record.PathfindingRuns - 1)} 趕路重走={record.TravelRewalks} 讓位={LedgerRules.Seconds(record.TravelYieldMs)} 返回失敗={current.ReturnFailuresAtEnd - current.ReturnFailuresAtSelect} "
            + $"進度@選={Progress(current.ProgressAtSelect)} ETA@選={current.EtaAtSelect} 進度@到={Progress(record.ProgressAtArrival)} 結束={(current.Ended ? LedgerRules.Label(current.End) : "?")} "
            + $"銀幣+={window.Silver} 金幣+={window.Gold} 輔助經驗+={window.PhantomExp} 知見+={window.Knowledge} 到帳={arrival} 結算={how} 窗長={LedgerRules.SignedSeconds(now - endedAt)}"));
    }

    private void LogCandidates(string kind, uint id, string name, ActivitySource source)
    {
        var line = new StringBuilder(256);
        line.Append(CultureInfo.InvariantCulture, $"[BOCCHI][選活動] kind={kind} id={id} name=「{name}」 來源={LedgerRules.Label(source)}");
        var config = automator?.Config;
        if (fates == null || config == null)
        {
            line.Append(" 候選=?");
            Svc.Log.Information(line.ToString());
            return;
        }

        var enabled = config.FatesMap;
        var candidates = fates.fates.Values.Where(f => enabled.TryGetValue(f.Id, out var on) && on).ToList();
        line.Append(CultureInfo.InvariantCulture, $" 候選={candidates.Count}");
        var player = Svc.Objects.LocalPlayer?.Position;
        for (var i = 0; i < candidates.Count && i < MaxCandidates; i++)
        {
            var fate = candidates[i];
            var position = fate.StartPosition;
            var distance = player is { } p ? Vector3.Distance(p, position).ToString("F1", CultureInfo.InvariantCulture) : "?";
            var navigation = "?";
            var cost = "?";
            if (player is { } from)
            {
                var shard = fate.Data.Aethernet?.GetData() ?? AethernetData.AllByDistance(position).First();
                var type = SmartNavigation.Cheapest(SmartNavigation.Costs(from, position, shard), out var best);
                navigation = type.ToString();
                cost = best.ToString("F1", CultureInfo.InvariantCulture);
            }

            var n = i + 1;
            line.Append(CultureInfo.InvariantCulture, $" 候選{n}.id={fate.Id} 候選{n}.名=「{fate.Name}」 候選{n}.進度={fate.CurrentProgress} 候選{n}.ETA={Eta(fate)} 候選{n}.距離={distance} 候選{n}.成本={cost} 候選{n}.導航={navigation}");
        }

        Svc.Log.Information(line.ToString());
    }

    private void EndIdle(long now, string next, string nextName)
    {
        idle = false;
        var position = Svc.Objects.LocalPlayer?.Position;
        Svc.Log.Information(string.Create(CultureInfo.InvariantCulture,
            $"[BOCCHI][待命] 結束 待命={LedgerRules.Seconds(now - idleSince)} 返回={idleReturns} 離營地={CampDistance(position)} 在營地={(CampDistanceValue(position) is { } d ? (d <= CampRadius ? "是" : "否") : "?")} 接著={next} 接著名=「{nextName}」"));
    }

    // ================================================================ 小工具

    // 這裡的處理器與插件其他 Framework.Update／聊天處理器共用 Dalamud 的 try/catch：擲出去會讓同一幀之後的處理器（動作鏈）被跳過。
    // 所以每個入口自己吃掉例外，同一個入口只寫一次 Warning。
    private void Fail(string where, Exception ex)
    {
        if (failedAt.Add(where))
        {
            Svc.Log.Warning($"[BOCCHI][場次記錄錯誤] {where}（之後同一處不再記；不影響自動化）：{ex.GetType().Name}：{ex.Message}");
        }
    }

    // 玩家打的字（說話、隊伍、通訊貝、跨服通訊貝、/echo…）不算：別人打出「獲得了…點知見。」不可以灌進帳，也不寫進 log。
    private static bool IsPlayerChat(XivChatType type)
    {
        return PlayerChat.IsPlayerChannel(type);
    }

    private static int SafeProgress(Activity activity)
    {
        try
        {
            return activity.ReadProgress();
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static string Eta(Fate fate)
    {
        var eta = fate.Progress.EstimateTimeToCompletion();
        return eta is { } span ? ((long)span.TotalSeconds).ToString(CultureInfo.InvariantCulture) : "?";
    }

    private static string Progress(int value)
    {
        return value < 0 ? "?" : value.ToString(CultureInfo.InvariantCulture);
    }

    private static string Level(int value)
    {
        return value <= 0 ? "?" : value.ToString(CultureInfo.InvariantCulture);
    }

    private static float? CampDistanceValue(Vector3? position)
    {
        return position is { } p && ZoneData.Aetherytes.TryGetValue(ZoneData.SOUTHHORN, out var camp) ? Vector3.Distance(p, camp) : null;
    }

    private static string CampDistance(Vector3? position)
    {
        return CampDistanceValue(position) is { } d ? d.ToString("F1", CultureInfo.InvariantCulture) : "?";
    }

    private static string Format(Vector3? position)
    {
        return position is { } p ? string.Create(CultureInfo.InvariantCulture, $"({p.X:F1},{p.Y:F1},{p.Z:F1})") : "?";
    }

    private sealed class ActivityEntry(Activity activity, string kind, string name, ActivitySource source)
    {
        public Activity? Activity = activity;

        public readonly ActivityRecord Record = activity.Record;

        public readonly string Kind = kind;

        public readonly uint Id = activity.data.Id;

        public readonly string Name = name;

        public readonly ActivitySource Source = source;

        public readonly ActivityIncomeWindow Window = new();

        public int ProgressAtSelect = -1;

        public string EtaAtSelect = "?";

        public int ReturnFailuresAtSelect;

        public int ReturnFailuresAtEnd;

        public ActivityState LastState;

        public bool Ended;

        public ActivityEnd End;

        public long EndedAt;
    }
}
