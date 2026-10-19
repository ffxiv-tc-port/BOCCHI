using System.Collections.Generic;

namespace BOCCHI.Modules.MapOverlay;

public enum MapOverlayGroup
{
    /// <summary>寶箱巡迴的規劃順序。</summary>
    Plan,

    /// <summary>寶箱生成點三態與已記錄的寶箱。</summary>
    Spawn,

    /// <summary>幸運胡蘿蔔巡迴進行中的生成點。</summary>
    Carrot,

    /// <summary>目前的趕路目的地。</summary>
    Destination,

    /// <summary>等魔法甕的出生點與等待點。</summary>
    Pot,
}

/// <summary>每一組要推什麼：簽章變了（或到了重推時間）才呼叫 <see cref="Build"/>。只在框架執行緒呼叫。</summary>
public interface IMapOverlaySource
{
    /// <returns>false＝這一組現在不用畫（之前推過的 key 會被拿掉）。</returns>
    bool TryGetSignature(MapOverlayGroup group, long now, out int signature);

    void Build(MapOverlayGroup group, List<OverlayLayer> into);
}

/// <summary>
/// 把各組疊加層推給 Mappy：每秒最多評估一次；一組的簽章變了或推過 <see cref="RefreshMs"/> 就整組重推，
/// 這一輪沒有、上一輪有的 key 呼叫 Remove。Mappy 不能用時什麼都不做；重新能用時全部重推。只在框架執行緒用。
/// </summary>
public sealed class MapOverlayPusher(IMapOverlaySink sink)
{
    public const int TtlMs = 30_000;

    public const long RefreshMs = TtlMs / 3;

    public const long MinIntervalMs = 1000;

    private static readonly MapOverlayGroup[] Groups = [MapOverlayGroup.Plan, MapOverlayGroup.Spawn, MapOverlayGroup.Carrot, MapOverlayGroup.Destination, MapOverlayGroup.Pot];

    private sealed class GroupState
    {
        public bool Pushed;

        public int Signature;

        public long RefreshAt;

        public readonly List<string> Keys = [];
    }

    private readonly GroupState[] states = [new(), new(), new(), new(), new()];

    private readonly List<OverlayLayer> buffer = [];

    private readonly List<string> keys = [];

    private long nextEvalAt;

    private int generation = -1;

    private bool anyPushed;

    /// <summary>每幀呼叫。<paramref name="wanted"/> false（設定關、不在島上）時清掉推過的全部。</summary>
    public void Tick(long now, bool wanted, IMapOverlaySource source)
    {
        if (!wanted)
        {
            Clear(now);
            return;
        }

        if (!sink.Poll(now))
        {
            // Mappy 不在了，它那邊的層也跟著不在：記下的狀態作廢，回來時全部重推。
            Forget();
            return;
        }

        if (sink.Generation != generation)
        {
            generation = sink.Generation;
            Forget();
            nextEvalAt = 0;
        }

        if (now < nextEvalAt)
        {
            return;
        }

        nextEvalAt = now + MinIntervalMs;
        for (var g = 0; g < Groups.Length; g++)
        {
            if (!Evaluate(Groups[g], states[g], now, source))
            {
                return;
            }
        }
    }

    /// <summary>推過東西就整個 source 清掉（離島、設定關、卸載）。</summary>
    public void Clear(long now)
    {
        if (anyPushed && sink.IsAvailable)
        {
            sink.ClearSource(now);
        }

        Forget();
    }

    // false＝呼叫失敗（Mappy 不能用了），這一輪到此為止。
    private bool Evaluate(MapOverlayGroup group, GroupState state, long now, IMapOverlaySource source)
    {
        if (!source.TryGetSignature(group, now, out var signature))
        {
            foreach (var key in state.Keys)
            {
                if (!sink.Remove(key, now))
                {
                    return false;
                }
            }

            state.Keys.Clear();
            state.Pushed = false;
            return true;
        }

        if (state.Pushed && state.Signature == signature && now < state.RefreshAt)
        {
            return true;
        }

        buffer.Clear();
        source.Build(group, buffer);
        keys.Clear();
        foreach (var layer in buffer)
        {
            if (!sink.Send(layer, TtlMs, now))
            {
                return false;
            }

            keys.Add(layer.Key);
            anyPushed = true;
        }

        foreach (var key in state.Keys)
        {
            if (!keys.Contains(key) && !sink.Remove(key, now))
            {
                return false;
            }
        }

        state.Keys.Clear();
        state.Keys.AddRange(keys);
        state.Pushed = true;
        state.Signature = signature;
        state.RefreshAt = now + RefreshMs;
        return true;
    }

    private void Forget()
    {
        foreach (var state in states)
        {
            state.Keys.Clear();
            state.Pushed = false;
        }

        anyPushed = false;
    }
}
