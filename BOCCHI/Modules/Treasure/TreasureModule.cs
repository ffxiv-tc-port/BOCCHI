using System;
using System.Collections.Generic;
using System.Numerics;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.StateManager;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Ocelot.Modules;
using Ocelot.Windows;

namespace BOCCHI.Modules.Treasure;

[OcelotModule(1003, 1)]
public class TreasureModule(Plugin _plugin, Config config) : Module(_plugin, config)
{
    public override TreasureConfig Config
    {
        get => PluginConfig.TreasureConfig;
    }

    public override bool ShouldInitialize
    {
        get => true;
    }

    public override bool IsEnabled
    {
        get => Config.IsPropertyEnabled(nameof(Config.Enabled));
    }

    public readonly static Vector4 Bronze = new(0.804f, 0.498f, 0.196f, 1f);

    public readonly static Vector4 Silver = new(0.753f, 0.753f, 0.753f, 1f);

    public readonly static Vector4 Unknown = new(0.6f, 0.2f, 0.8f, 1f);

    public readonly TreasureTracker Tracker = new();

    private TreasureHunt hunter = null!;

    /// <summary>PostInitialize 之前（或它失敗時）是 null。</summary>
    public TreasureHunt? Hunter
    {
        get => hunter;
    }

    public List<Treasure> Treasures
    {
        get => Tracker.Treasures;
    }

    private readonly Panel panel = new();

    private readonly Radar radar = new();

    private readonly SilverChestAlerter silverChestAlerter = new();

    private const string MemoryTag = "[BOCCHI][寶箱記錄]";

    /// <summary>寶箱巡迴跨越單次巡迴的記錄；只在框架執行緒讀寫。</summary>
    public readonly TreasureHuntMemory HuntMemory = new();

    private const string HuntTag = "[BOCCHI][寶箱巡迴]";

    /// <summary>魔尋寶感知到的寶箱數；參與 F.A.T.E.／緊急遭遇戰、按清除、換區、登出、開的比感知的多就作廢（舊數字留著給主視窗顯示，登出才丟）。只在框架執行緒讀寫。</summary>
    public readonly TreasureSenseCount SenseCount = new();

    // 已經取用到的 TreasureSenseNotice.Sequence。
    private int senseSequence;

    /// <summary>已經取用到的 <see cref="TreasureSenseNotice.Sequence"/>：相等＝最新的魔尋寶結果已經套用（或判定不用）。</summary>
    public int ConsumedSenseSequence
    {
        get => senseSequence;
    }

    private SeenTreasure[] seenBuffer = new SeenTreasure[16];

    private readonly List<TreasureSpawnPoint> spawnBuffer = [];

    private StateManagerModule? states;

    private CriticalEncountersModule? encounters;

    // 巡迴在跑時進入 F.A.T.E. 範圍（路過）先不算參與；巡迴停下後還在範圍內打起來才補算。
    private bool fatePassThrough;

    private bool inFate;

    private bool inCriticalEncounter;

    private bool subscribed;

    public override void PostInitialize()
    {
        hunter = new TreasureHunt(this);
        TryGetModule(out states);
        TryGetModule(out encounters);
        if (states != null)
        {
            states.OnEnterInFate += OnEnterFate;
            states.OnExitInFate += OnExitFate;
            states.OnEnterInCriticalEncounter += OnEnterCriticalEncounter;
            states.OnExitInCriticalEncounter += OnExitCriticalEncounter;
        }

        Svc.ClientState.TerritoryChanged += OnTerritoryChangedForMemory;
        Svc.ClientState.Logout += OnLogoutForMemory;
        subscribed = true;
    }

    public override void Dispose()
    {
        if (subscribed)
        {
            subscribed = false;
            Svc.ClientState.TerritoryChanged -= OnTerritoryChangedForMemory;
            Svc.ClientState.Logout -= OnLogoutForMemory;
            if (states != null)
            {
                states.OnEnterInFate -= OnEnterFate;
                states.OnExitInFate -= OnExitFate;
                states.OnEnterInCriticalEncounter -= OnEnterCriticalEncounter;
                states.OnExitInCriticalEncounter -= OnExitCriticalEncounter;
            }
        }

        base.Dispose();
    }

    public override void Update(UpdateContext context)
    {
        Tracker.Tick(Plugin);
        silverChestAlerter.Tick(this, PluginConfig.NotificationsConfig.PraiseOccultEvents);
        ObserveForHunt();
        ConsumeSense();
        hunter.Update();
        if (Config.ShouldEnableTreasureHunt && ZoneData.IsInOccultCrescent())
        {
            hunter.UpdateRouteBoard();
        }
    }

    /// <summary>清除寶箱記錄與巡過（主視窗按鈕）；暫停中的巡迴進度改成續跑時全部重新規劃。</summary>
    public void ClearHuntMemory(string source)
    {
        var (chests, visited) = HuntMemory.ClearRecords();
        var forgot = hunter?.Progress.ForgetVisited() == true;
        Svc.Log.Information($"{MemoryTag} 清除記錄（{source}）：寶箱 {chests} 個、巡過的生成點 {visited} 個"
                            + (forgot ? "；這一輪已跑的點也一併作廢，續跑時全部重新規劃" : string.Empty));
        InvalidateSense("清除記錄");
    }

    /// <summary>巡迴可以跳過這個生成點：魔尋寶感知到的那一種位置都知道了（開掉的＋已記錄還沒開的達到感知數），而且那裡沒有已記錄的寶箱。生成點還沒讀到時一律 false。</summary>
    public bool SenseSkips(uint node)
    {
        if (!SenseCount.Known || !Config.HuntSkipBySenseCount || !HuntMemory.TryGetSpawnPoint(node, out var point))
        {
            return false;
        }

        return SenseCount.Skips(point.Type, HuntMemory.HasChestAt(node), HuntMemory.OpenedCount(point.Type), HuntMemory.CountChests(point.Type));
    }

    /// <summary>這一種的位置都知道了（規則見 <see cref="TreasureSenseCount.IsExhausted(TreasureType, int, int)"/>）。</summary>
    public bool SenseExhausted(TreasureType type)
    {
        return SenseCount.IsExhausted(type, HuntMemory.OpenedCount(type), HuntMemory.CountChests(type));
    }

    /// <summary>給記錄：感知數與之後開了幾個（「銀 開 a／感知 S、銅 開 b／感知 B」），有已記錄還沒開的就附在後面。</summary>
    public string SenseProgressText()
    {
        return $"銀 {SenseProgress(TreasureType.Silver)}、銅 {SenseProgress(TreasureType.Bronze)}";
    }

    private string SenseProgress(TreasureType type)
    {
        var recorded = HuntMemory.CountChests(type);
        return $"開 {SenseCount.OpenedSince(type, HuntMemory.OpenedCount(type))}／感知 {SenseCount.Sensed(type)}"
               + (recorded > 0 ? $"（另有已記錄還沒開的 {recorded}）" : string.Empty);
    }

    private bool SenseOverrun(TreasureType type, bool withRecorded)
    {
        return SenseCount.IsOverrun(type, HuntMemory.OpenedCount(type), withRecorded ? HuntMemory.CountChests(type) : 0);
    }

    // 每幀：取用聊天裡新的魔尋寶結果；有數字時檢查開掉的＋已記錄還沒開的是不是比感知的多（之後又長出寶箱）。
    private void ConsumeSense()
    {
        if (TreasureSenseNotice.Sequence != senseSequence)
        {
            senseSequence = TreasureSenseNotice.Sequence;
            ApplySense(TreasureSenseNotice.Silver, TreasureSenseNotice.Bronze, Environment.TickCount64);
        }

        if (SenseCount.Known && (SenseOverrun(TreasureType.Silver, true) || SenseOverrun(TreasureType.Bronze, true)))
        {
            var openedOnly = SenseOverrun(TreasureType.Silver, false) || SenseOverrun(TreasureType.Bronze, false);
            InvalidateSense($"{(openedOnly ? "開的比感知的多" : "開的加上已記錄還沒開的比感知的多")}：{SenseProgressText()}，感知之後又長出寶箱");
        }
    }

    // 聊天那端只收島上的訊息、離島時 ResetMemory 丟掉沒取用的，這裡不再判區域。
    private void ApplySense(int silver, int bronze, long now)
    {
        var knownSilver = HuntMemory.CountChests(TreasureType.Silver);
        var knownBronze = HuntMemory.CountChests(TreasureType.Bronze);
        var position = Player.Available ? Player.Position : default;
        var head = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{HuntTag} 魔尋寶感知：銀 {silver}、銅 {bronze}（區域 {Svc.ClientState.TerritoryType}、位置 ({position.X:F1}, {position.Y:F1}, {position.Z:F1})；"
            + $"已記錄還沒開的 銀 {knownSilver}、銅 {knownBronze}；生成點 {(HuntMemory.HasSpawnPoints ? $"{HuntMemory.SpawnPointCount} 個" : "未讀（進島後第一次開始寶箱巡迴才讀，不影響計數）")}）");
        var openedSilver = HuntMemory.OpenedCount(TreasureType.Silver);
        var openedBronze = HuntMemory.OpenedCount(TreasureType.Bronze);
        if (HuntMemory.Participating)
        {
            SenseCount.SenseUnused(silver, bronze, now, openedSilver, openedBronze, 1);
            Svc.Log.Information(head + "：正在參與 F.A.T.E.／緊急遭遇戰，參與後寶箱可能刷新，這次的數字不用");
            return;
        }

        if (knownSilver > silver || knownBronze > bronze)
        {
            SenseCount.SenseUnused(silver, bronze, now, openedSilver, openedBronze, 0);
            Svc.Log.Information(head + "：記錄中還沒開的寶箱比感知數多（感知數可能只算附近一區），這次的數字不用");
            return;
        }

        SenseCount.Sense(silver, bronze, now, openedSilver, openedBronze);
        Svc.Log.Information(head + (Config.HuntSkipBySenseCount
            ? "：之後某一種開掉的加上已記錄還沒開的達到這個數，寶箱巡迴就跳過那一種剩下的生成點"
            : "：「開滿感知數就跳過剩下的生成點」已關閉，只記錄"));
    }

    private void InvalidateSense(string reason)
    {
        if (SenseCount.Invalidate())
        {
            Svc.Log.Information($"{HuntTag} 魔尋寶感知數作廢（{reason}）：寶箱巡迴照常巡全部生成點，等下次魔尋寶");
        }
    }

    /// <summary>
    /// 寶箱巡迴規劃時讀到的版面生成點（只認 TreasureData.Levels 裡的）交給記錄。版面只在使用者開始／續跑巡迴時讀，
    /// 所以進島後到第一次規劃之前不記巡過（看到的寶箱照記，生成點到了再對上）。
    /// </summary>
    public void UseSpawnPoints(List<TreasureData.TreasureDatum> layout)
    {
        spawnBuffer.Clear();
        foreach (var datum in layout)
        {
            if (TreasureData.Levels.ContainsKey(datum.Id))
            {
                spawnBuffer.Add(new TreasureSpawnPoint(datum.Id, datum.Position, datum.Type == TreasureLayout.SilverModel ? TreasureType.Silver : TreasureType.Bronze));
            }
        }

        if (spawnBuffer.Count == 0 || (HuntMemory.HasSpawnPoints && HuntMemory.SpawnPointCount == spawnBuffer.Count))
        {
            return;
        }

        HuntMemory.SetSpawnPoints(spawnBuffer);
        Svc.Log.Debug($"{MemoryTag} 讀到寶箱生成點 {spawnBuffer.Count} 個（版面裡青銅／白銀寶箱 {layout.Count} 個），開始記錄巡過的點");
    }

    // 每幀：讀圖中只記時間；在島上就把看得到的寶箱交給記錄。不讀遊戲記憶體（只用 TreasureTracker 這一幀的清單）、不配置。
    private void ObserveForHunt()
    {
        var now = Environment.TickCount64;
        if (!ZoneData.IsInOccultCrescent() || !Player.Available)
        {
            return;
        }

        if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51])
        {
            HuntMemory.NoteAreaTransition(now);
            return;
        }

        // 巡迴停下後還在範圍內、而且打起來了才補算（只是剛好停在範圍裡不算）。
        if (fatePassThrough && inFate && hunter?.IsRunning != true && Svc.Condition[ConditionFlag.InCombat])
        {
            fatePassThrough = false;
            CountParticipation("F.A.T.E.（巡迴停下後在範圍內戰鬥）", ReadFateKey());
        }

        var count = CollectSeen();
        HuntMemory.Observe(now, Player.Position, PluginConfig.PathfinderConfig.DetectionRange, seenBuffer.AsSpan(0, count));
        LogMemoryEvents();
    }

    private int CollectSeen()
    {
        var count = 0;
        var treasures = Tracker.Treasures;
        for (var i = 0; i < treasures.Count; i++)
        {
            var treasure = treasures[i];
            var type = treasure.GetTreasureType();
            if (type is not (TreasureType.Bronze or TreasureType.Silver))
            {
                continue;
            }

            if (count == seenBuffer.Length)
            {
                Array.Resize(ref seenBuffer, Math.Max(16, seenBuffer.Length * 2));
            }

            seenBuffer[count++] = new SeenTreasure(treasure.LastKnownPosition, type, treasure.IsOpened);
        }

        return count;
    }

    private void LogMemoryEvents()
    {
        var events = HuntMemory.Events;
        if (events.Count == 0)
        {
            return;
        }

        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            var what = e.Change switch
            {
                TreasureMemoryChange.ChestAdded => "記下寶箱",
                TreasureMemoryChange.ChestOpened => "寶箱開了，移出記錄",
                TreasureMemoryChange.ChestGone => "走近了看不到寶箱，移出記錄",
                TreasureMemoryChange.NodeVisited => "巡過生成點",
                TreasureMemoryChange.NodeReopened => "巡過的生成點又看到寶箱，改回沒巡過",
                _ => e.Change.ToString(),
            };
            var line = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{MemoryTag} {what}：{(e.Type == TreasureType.Silver ? "白銀寶箱" : "青銅寶箱")} 生成點 {(e.Node == 0 ? "?" : e.Node.ToString())} 位置 ({e.Position.X:F1}, {e.Position.Y:F1}, {e.Position.Z:F1})；"
                + $"記錄 寶箱 {HuntMemory.ChestCount} 個、巡過 {HuntMemory.VisitedCount} 個");

            // 記錄過期（例：離島前記的寶箱回來後不在了）很少見，要看得到。
            if (e.Change == TreasureMemoryChange.ChestGone)
            {
                Svc.Log.Information(line);
            }
            else
            {
                Svc.Log.Debug(line);
            }
        }

        HuntMemory.ClearEvents();
    }

    // 參與只讓記錄進入「參與中」（不記巡過）；記錄與寶箱巡迴這一輪已跑的點都不清，要重巡由使用者按清除。
    private void CountParticipation(string what, ulong key)
    {
        if (!HuntMemory.NoteParticipation(key))
        {
            return;
        }

        Svc.Log.Debug($"{MemoryTag} 參與{what}：巡過的生成點 {HuntMemory.VisitedCount} 個、已記錄的寶箱 {HuntMemory.ChestCount} 個、前往失敗 {HuntMemory.UnreachableCount} 個"
                      + $"、這一輪已跑 {hunter?.Progress.VisitedCount ?? 0} 點都保留（參與不清；要重巡請按清除）");
        SenseCount.NoteEvent();
        InvalidateSense($"參與{what}，之後寶箱可能刷新");
    }

    private void OnEnterFate(StateManagerModule _)
    {
        try
        {
            inFate = true;
            if (!ZoneData.IsInOccultCrescent())
            {
                return;
            }

            if (hunter?.IsRunning == true)
            {
                fatePassThrough = true;
                Svc.Log.Debug($"{MemoryTag} 寶箱巡迴路過 F.A.T.E. 範圍，不算參與");
                return;
            }

            CountParticipation("F.A.T.E.", ReadFateKey());
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"{MemoryTag} 處理進入 F.A.T.E. 時出錯：{ex.GetType().Name}：{ex.Message}");
        }
    }

    private void OnExitFate(StateManagerModule _)
    {
        inFate = false;
        if (fatePassThrough)
        {
            fatePassThrough = false;
            return;
        }

        if (!inCriticalEncounter)
        {
            HuntMemory.EndParticipation();
        }
    }

    private void OnEnterCriticalEncounter(StateManagerModule _)
    {
        try
        {
            inCriticalEncounter = true;
            if (ZoneData.IsInOccultCrescent())
            {
                CountParticipation("緊急遭遇戰", ReadCriticalEncounterKey());
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"{MemoryTag} 處理進入緊急遭遇戰時出錯：{ex.GetType().Name}：{ex.Message}");
        }
    }

    private void OnExitCriticalEncounter(StateManagerModule _)
    {
        inCriticalEncounter = false;
        if (!inFate || fatePassThrough)
        {
            HuntMemory.EndParticipation();
        }
    }

    // 同一場的辨識與新月島記錄相同：場次 id＋開始時間；讀不到開始時間回 0（一律算新的一場）。
    private static unsafe ulong ReadFateKey()
    {
        var manager = FateManager.Instance();
        if (manager == null || manager->CurrentFate == null)
        {
            return 0;
        }

        var fate = manager->CurrentFate;
        var start = (uint)fate->StartTimeEpoch;
        return start == 0 ? 0 : ((ulong)fate->FateId << 32) | start;
    }

    private unsafe ulong ReadCriticalEncounterKey()
    {
        var container = DynamicEventContainer.GetInstance();
        if (container == null || container->CurrentEventId == 0)
        {
            return 0;
        }

        var id = container->CurrentEventId;
        var start = encounters != null && encounters.CriticalEncounters.TryGetValue(id, out var snapshot) ? (uint)snapshot.StartTimestamp : 0;
        return start == 0 ? 0 : ((ulong)id << 32) | start;
    }

    private void OnTerritoryChangedForMemory(ushort territory)
    {
        ResetMemory(territory == ZoneData.SOUTHHORN ? "換區（含換副本實例）" : "離島", true);
    }

    private void OnLogoutForMemory(int type, int code)
    {
        ResetMemory("登出", false);
    }

    // keepChests＝換區、離島：寶箱留著；登出才連寶箱一起清（下次登入的可能是別的角色）。
    private void ResetMemory(string reason, bool keepChests)
    {
        var (chests, visited) = keepChests ? HuntMemory.LeaveArea() : HuntMemory.Reset();
        InvalidateSense(reason);
        if (!keepChests)
        {
            SenseCount.Forget();
        }

        senseSequence = TreasureSenseNotice.Sequence;
        fatePassThrough = false;
        inFate = false;
        inCriticalEncounter = false;
        if (chests + visited > 0)
        {
            Svc.Log.Information(keepChests
                ? $"{MemoryTag} {reason}：清空巡過的生成點 {visited} 個；已記錄的寶箱 {chests} 個留著（回到島上、讀到生成點後重新對上；走近了看不到才移出）"
                : $"{MemoryTag} {reason}：清空寶箱記錄 {chests} 個、巡過的生成點 {visited} 個");
        }
    }

    public override void OnTerritoryChanged(ushort id)
    {
        silverChestAlerter.Reset();
    }

    public override void Render(RenderContext context)
    {
        radar.Draw(context, this);
    }

    public override bool RenderMainUi(RenderContext context)
    {
        panel.Draw(this);

        if (Config.ShouldEnableTreasureHunt)
        {
            hunter.Draw(this);
        }

        return true;
    }
}
