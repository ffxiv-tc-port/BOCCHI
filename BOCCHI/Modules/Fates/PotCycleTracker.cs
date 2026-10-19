using System;
using System.Collections.Generic;
using BOCCHI.Data;
using ECommons.DalamudServices;
using ExcelFate = Lumina.Excel.Sheets.Fate;

namespace BOCCHI.Modules.Fates;

/// <summary>
/// 每幀把 F.A.T.E. 表上的魔法甕餵給 <see cref="PotCycle" />，並在出現／結束／推估落空時各寫一行 Information。
/// 只在 framework 執行緒用；讀的是 <see cref="FateTracker" /> 已抄好的受管值，不碰原生記憶體。
/// 進入新月島、登出時清空：週期只對同一座島有效。
/// </summary>
public sealed class PotCycleTracker : IDisposable
{
    public readonly PotCycle Cycle = new();

    /// <summary>新出現（或進島後第一次看到）的魔法甕：F.A.T.E. id、名稱。訂閱端自己吞例外。</summary>
    public event Action<uint, string>? OnPotSpawned;

    /// <summary>推估清空了（換了一座島或登出）。訂閱端不可擲例外。</summary>
    public event Action? OnCycleReset;

    private readonly Dictionary<uint, string> names = new();

    // 已寫過 log 的「推估落空」輪數：同一輪只寫一次。
    private int loggedMissed;

    // 上一幀用的週期：使用者改了週期時，已經落空的輪數照新週期重算、不補寫 log。
    private TimeSpan lastPeriod;

    public PotCycleTracker()
    {
        Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
        Svc.ClientState.Logout += OnLogout;
    }

    public void Update(FateTracker tracker, TimeSpan period)
    {
        var now = DateTime.UtcNow;
        Track(tracker, PotCycle.PersistentPots, now, period);
        Track(tracker, PotCycle.PleadingPots, now, period);

        if (period != lastPeriod)
        {
            if (lastPeriod != default)
            {
                loggedMissed = Cycle.TryPredict(now, period, out _, out _, out var missedNow) ? missedNow : 0;
            }

            lastPeriod = period;
        }

        if (Cycle.ActiveId != 0 || !Cycle.TryPredict(now, period, out _, out _, out var missed) || missed <= loggedMissed)
        {
            return;
        }

        loggedMissed = missed;
        var missedId = missed % 2 == 1 ? PotCycle.Opposite(Cycle.AnchorId) : Cycle.AnchorId;
        var missedAt = Cycle.AnchorSpawnUtc + TimeSpan.FromTicks(period.Ticks * missed);
        Svc.Log.Information(
            $"[BOCCHI][魔法甕] 推估 {Local(missedAt)} 出現的「{GetName(missedId)}」過了 {PotCycle.Grace.TotalMinutes:0} 分鐘仍沒看到（連續 {missed} 輪；週期 {period.TotalMinutes:0} 分可能不準，或那段時間不在島上／不在可更新狀態）");
    }

    private void Track(FateTracker tracker, uint id, DateTime now, TimeSpan period)
    {
        if (!tracker.Fates.TryGetValue(id, out var fate))
        {
            if (Cycle.Ended(id))
            {
                var lasted = Cycle.AnchorId == id ? FormatDuration(now - Cycle.AnchorSpawnUtc) : "?";
                Svc.Log.Information($"[BOCCHI][魔法甕] 「{GetName(id)}」已不在 F.A.T.E. 清單上（從出現算 {lasted}）");
            }

            return;
        }

        var epoch = fate.StartTimeEpoch;
        var fromGame = epoch > 0;
        var spawn = fromGame ? DateTime.UnixEpoch.AddSeconds(epoch) : now;

        // 推估要在 Observe 之前取，log 才能對照「原本推的是什麼」。
        var hadPrediction = Cycle.TryPredict(now, period, out var expectedId, out var expectedAt, out _);
        var previousId = Cycle.AnchorId;
        var previousFromGame = Cycle.AnchorFromGameTime;
        var firstSeen = Cycle.AnchorSpawnUtc;

        switch (Cycle.Observe(id, spawn, fromGame))
        {
            case PotObservation.NewSpawn:
                // Fate.Name 在遊戲還沒填好名稱時是固定的後備字樣，不能記下來。
                if (fate.Name != Fate.UnknownName)
                {
                    names[id] = fate.Name;
                }

                loggedMissed = 0;
                LogSpawn(id, spawn, fromGame, previousId, previousFromGame, hadPrediction, expectedId, expectedAt, period);
                OnPotSpawned?.Invoke(id, GetName(id));
                break;

            case PotObservation.Refined:
                Svc.Log.Information($"[BOCCHI][魔法甕] 「{GetName(id)}」改用遊戲記錄的開始時間 {Local(spawn)}（第一次看到是 {Local(firstSeen)}）");
                break;
        }
    }

    private void LogSpawn(uint id, DateTime spawn, bool fromGame, uint previousId, bool previousFromGame, bool hadPrediction, uint expectedId, DateTime expectedAt, TimeSpan period)
    {
        var source = fromGame ? "遊戲記錄的開始時間" : "第一次看到的時間，遊戲沒給開始時間";
        if (previousId == 0 || Cycle.LastInterval is not { } interval)
        {
            Svc.Log.Information($"[BOCCHI][魔法甕] 看到「{GetName(id)}」出現：{Local(spawn)}（{source}）；本次進島第一次看到，之後依 {period.TotalMinutes:0} 分週期推估");
            return;
        }

        var precision = fromGame && previousFromGame ? "兩次都用遊戲時間" : "至少一次是看到的時間，偏差可能較大";
        var expectation = hadPrediction
            ? $"；原推估「{GetName(expectedId)}」{Local(expectedAt)}，實際差 {FormatDelta(spawn - expectedAt)}{(expectedId == id ? string.Empty : "（出現的不是推估的那一個）")}"
            : string.Empty;
        Svc.Log.Information(
            $"[BOCCHI][魔法甕] 看到「{GetName(id)}」出現：{Local(spawn)}（{source}）；距上次「{GetName(previousId)}」{FormatDuration(interval)}（{precision}；設定週期 {period.TotalMinutes:0} 分）{expectation}");
    }

    /// <summary>魔法甕名稱：看過的用遊戲給的名稱，沒看過的查 F.A.T.E. 表；查不到回 #id。</summary>
    public string GetName(uint id)
    {
        if (names.TryGetValue(id, out var name))
        {
            return name;
        }

        name = Svc.Data?.GetExcelSheet<ExcelFate>().GetRowOrDefault(id)?.Name.ExtractText() ?? string.Empty;
        if (name.Length == 0)
        {
            name = EventData.Fates.TryGetValue(id, out var data) ? data.InternalName : $"#{id}";
        }

        names[id] = name;
        return name;
    }

    private void OnTerritoryChanged(ushort territory)
    {
        if (territory != ZoneData.SOUTHHORN || Cycle.AnchorId == 0)
        {
            return;
        }

        Clear("進入新月島：上一座島的魔法甕推估已清除，看到下一次出現才會重新推估");
    }

    private void OnLogout(int type, int code)
    {
        if (Cycle.AnchorId == 0)
        {
            return;
        }

        Clear("登出：魔法甕推估已清除，下次進島看到出現才會重新推估");
    }

    private void Clear(string reason)
    {
        Cycle.Reset();
        loggedMissed = 0;
        Svc.Log.Information($"[BOCCHI][魔法甕] {reason}");
        OnCycleReset?.Invoke();
    }

    private static string Local(DateTime utc)
    {
        return utc.ToLocalTime().ToString("HH:mm:ss");
    }

    /// <summary>「分:秒」。</summary>
    public static string FormatDuration(TimeSpan span)
    {
        var seconds = (long)Math.Round(Math.Abs(span.TotalSeconds));
        return $"{(span < TimeSpan.Zero ? "-" : string.Empty)}{seconds / 60}:{seconds % 60:D2}";
    }

    /// <summary>「±分:秒」：正＝比推估晚。</summary>
    public static string FormatDelta(TimeSpan span)
    {
        return (span < TimeSpan.Zero ? string.Empty : "+") + FormatDuration(span);
    }

    public void Dispose()
    {
        Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Svc.ClientState.Logout -= OnLogout;
    }
}
