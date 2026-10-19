using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using ECommons.DalamudServices;

namespace BOCCHI.Modules.MapOverlay;

/// <summary>推送端看到的 Mappy：可不可以用、送一層、拿掉一層、整個 source 清掉。</summary>
public interface IMapOverlaySink
{
    bool IsAvailable { get; }

    /// <summary>每次從不能用變成能用就加一（Mappy 重新載入後它那邊是空的，推送端要全部重推）。</summary>
    int Generation { get; }

    /// <summary>每幀呼叫：不能用時每 <see cref="MappyOverlayClient.ProbeIntervalMs"/> 問一次 ApiVersion。回傳現在能不能用。</summary>
    bool Poll(long now);

    /// <returns>false＝呼叫失敗（已轉成不能用）；Mappy 回 false（拒收）仍算送到。</returns>
    bool Send(OverlayLayer layer, int ttlMs, long now);

    bool Remove(string key, long now);

    bool ClearSource(long now);
}

/// <summary>
/// Mappy.Overlay 的呼叫端。契約（名字與型別逐字照 Mappy 那邊，不可改）：ApiVersion Func&lt;int&gt;（1）；
/// SetPath／SetPoints(string source, string key, uint territoryId, Vector3[] points, uint rgb, int flags, string[]? labels, int ttlMs) -> bool；
/// Remove(string source, string key) -> bool；ClearSource(string source) -> bool。只在框架執行緒呼叫，不擲出。
/// </summary>
/// <remarks>
/// 一律傳 Vector3[]／string[]（不傳 List）：型別不合時 CallGate 會靜默走一趟 JSON 來回。
/// 例外全部自己接：EzIPC 的 SafeWrapper 只攔 IpcNotReadyError，型別不合會一路擲出去。Mappy 沒裝或沒載入不寫記錄。
/// </remarks>
public sealed class MappyOverlayClient : IMapOverlaySink
{
    public const string Source = "BOCCHI";

    public const long ProbeIntervalMs = 5000;

    public const int SupportedApiVersion = 1;

    private readonly uint territoryId;

    private readonly ICallGateSubscriber<int>? apiVersion;

    private readonly ICallGateSubscriber<string, string, uint, Vector3[], uint, int, string[]?, int, bool>? setPath;

    private readonly ICallGateSubscriber<string, string, uint, Vector3[], uint, int, string[]?, int, bool>? setPoints;

    private readonly ICallGateSubscriber<string, string, bool>? remove;

    private readonly ICallGateSubscriber<string, bool>? clearSource;

    private long nextProbeAt;

    private bool failureReported;

    private readonly HashSet<string> rejectedReported = [];

    public MappyOverlayClient(IDalamudPluginInterface pluginInterface, uint territoryId)
    {
        this.territoryId = territoryId;
        try
        {
            apiVersion = pluginInterface.GetIpcSubscriber<int>("Mappy.Overlay.ApiVersion");
            setPath = pluginInterface.GetIpcSubscriber<string, string, uint, Vector3[], uint, int, string[]?, int, bool>("Mappy.Overlay.SetPath");
            setPoints = pluginInterface.GetIpcSubscriber<string, string, uint, Vector3[], uint, int, string[]?, int, bool>("Mappy.Overlay.SetPoints");
            remove = pluginInterface.GetIpcSubscriber<string, string, bool>("Mappy.Overlay.Remove");
            clearSource = pluginInterface.GetIpcSubscriber<string, bool>("Mappy.Overlay.ClearSource");
        }
        catch (Exception ex)
        {
            Report(ex, "取得 Mappy.Overlay 呼叫端");
        }
    }

    public bool IsAvailable { get; private set; }

    public int Generation { get; private set; }

    public bool Poll(long now)
    {
        if (IsAvailable)
        {
            return true;
        }

        if (now < nextProbeAt || apiVersion == null)
        {
            return false;
        }

        nextProbeAt = now + ProbeIntervalMs;
        int version;
        try
        {
            version = apiVersion.InvokeFunc();
        }
        catch (IpcNotReadyError)
        {
            return false;
        }
        catch (Exception ex)
        {
            Report(ex, "Mappy.Overlay.ApiVersion");
            return false;
        }

        if (version < SupportedApiVersion)
        {
            if (!failureReported)
            {
                failureReported = true;
                Svc.Log.Information($"[BOCCHI][Mappy] Mappy.Overlay.ApiVersion 回 {version}（要 {SupportedApiVersion} 以上）：不推送巡迴路線與生成點");
            }

            return false;
        }

        IsAvailable = true;
        Generation++;
        rejectedReported.Clear();
        Svc.Log.Information($"[BOCCHI][Mappy] 找到 Mappy 疊加層（ApiVersion {version}）：開始推送巡迴路線與生成點");
        return true;
    }

    public bool Send(OverlayLayer layer, int ttlMs, long now)
    {
        var gate = layer.Shape == OverlayShape.Path ? setPath : setPoints;
        if (!IsAvailable || gate == null)
        {
            return false;
        }

        try
        {
            if (!gate.InvokeFunc(Source, layer.Key, territoryId, layer.Points, layer.Rgb, layer.Flags, layer.Labels, ttlMs) && rejectedReported.Add(layer.Key))
            {
                Svc.Log.Information($"[BOCCHI][Mappy] Mappy 拒收「{layer.Key}」（{layer.Shape}，{layer.Points.Length} 點）；同一個 key 這次連線不再記");
            }

            return true;
        }
        catch (Exception ex)
        {
            Lost(ex, layer.Shape == OverlayShape.Path ? "SetPath" : "SetPoints", now);
            return false;
        }
    }

    public bool Remove(string key, long now)
    {
        if (!IsAvailable || remove == null)
        {
            return false;
        }

        try
        {
            remove.InvokeFunc(Source, key);
            return true;
        }
        catch (Exception ex)
        {
            Lost(ex, "Remove", now);
            return false;
        }
    }

    public bool ClearSource(long now)
    {
        if (!IsAvailable || clearSource == null)
        {
            return false;
        }

        try
        {
            clearSource.InvokeFunc(Source);
            return true;
        }
        catch (Exception ex)
        {
            Lost(ex, "ClearSource", now);
            return false;
        }
    }

    // 呼叫失敗就當 Mappy 不在了，照樣每 ProbeIntervalMs 重問 ApiVersion；卸載（IpcNotReadyError）不寫記錄，其他只寫第一次。
    private void Lost(Exception ex, string what, long now)
    {
        IsAvailable = false;
        nextProbeAt = now + ProbeIntervalMs;
        if (ex is not IpcNotReadyError)
        {
            Report(ex, "Mappy.Overlay." + what);
        }
    }

    private void Report(Exception ex, string what)
    {
        if (failureReported)
        {
            return;
        }

        failureReported = true;
        Svc.Log.Information($"[BOCCHI][Mappy] 呼叫 {what} 失敗（{ex.GetType().Name}：{ex.Message}）：先不推送，每 {ProbeIntervalMs / 1000} 秒重試；之後的失敗不再記");
    }
}
