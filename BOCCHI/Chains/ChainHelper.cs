using System;
using System.Numerics;
using BOCCHI.Automation;
using BOCCHI.Enums;
using BOCCHI.Modules.Automator;
using BOCCHI.Modules.Mount;
using BOCCHI.Modules.Mount.Chains;
using BOCCHI.Modules.Teleporter;
using BOCCHI.Modules.Treasure;
using BOCCHI.Pathfinding;
using Dalamud.Game.ClientState.Conditions;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;
using Ocelot.IPC;
using Ocelot.Modules;

namespace BOCCHI.Chains;

public class ChainHelper
{
    private const int ArriveTimeLimitMs = 30000;

    private static ChainHelper? _instance = null;

    private static ChainHelper Instance
    {
        get
        {
            if (_instance == null)
            {
                throw new InvalidOperationException("ChainHelper has not been initialized. Call Initialize(plugin) first.");
            }

            return _instance;
        }
    }

    private readonly Plugin Plugin;

    private static ModuleManager Modules
    {
        get => Instance.Plugin.Modules;
    }

    private static IPCManager IPC
    {
        get => Instance.Plugin.IPC;
    }

    private ChainHelper(Plugin plugin)
    {
        Plugin = plugin;
    }

    public static void Initialize(Plugin plugin)
    {
        _instance ??= new ChainHelper(plugin);
    }

    public static ReturnChain ReturnChain()
    {
        var config = new ReturnChainConfig
        {
            ApproachAetheryte = Instance.Plugin.Config.TeleporterConfig.ApproachAetheryte,
        };

        return ReturnChain(config);
    }

    public static ReturnChain ReturnChain(ReturnChainConfig config)
    {
        return new ReturnChain(Modules.GetModule<TeleporterModule>(), config);
    }

    public static TeleportChain TeleportChain(Aethernet aethernet)
    {
        return new TeleportChain(
            aethernet,
            IPC.GetSubscriber<Lifestream>(),
            Modules.GetModule<TeleporterModule>()
        );
    }

    public static MountChain MountChain()
    {
        return new MountChain(Modules.GetModule<MountModule>().Config);
    }

    // 這兩個方法的內層子鏈必須寫成零參數 lambda，才會綁到 ConditionalThen(條件, Func<Chain>)：
    // 外層等子鏈跑完、中止外層時子鏈一起中止。寫成 `_ =>` 會綁到 Action 多載，子鏈脫鉤執行，
    // 外層不等它、全部停止也停不到它。
    // 停下不等於走到（路徑提早結束、被停下、算路空窗）：停下時重新量距離，不夠近就重走，有上限（ArrivalGuard）；
    // 重走到上限仍到不了就照舊往下，由呼叫端自己看距離。onFinished 帶這次的判斷，只在 framework 執行緒呼叫。
    public static Func<Chain> PathfindToAndWait(Vector3 destination, float distance, TravelSource source)
    {
        return PathfindToAndWait(destination, distance, source, null);
    }

    public static Func<Chain> PathfindToAndWait(Vector3 destination, float distance, TravelSource source, Action<ArrivalGuard>? onFinished)
    {
        return PathfindToAndWait(destination, distance, source, onFinished, null);
    }

    /// <param name="yieldToUser">每拍問一次要不要讓位給使用者自己的操作（<see cref="ArrivalYieldGate"/>）；null 或回 false 時與沒有這個參數時相同。</param>
    public static Func<Chain> PathfindToAndWait(Vector3 destination, float distance, TravelSource source, Action<ArrivalGuard>? onFinished, Func<bool>? yieldToUser)
    {
        var vnav = IPC.GetSubscriber<VNavmesh>();
        return () =>
        {
            var guard = new ArrivalGuard();
            var walk = yieldToUser == null ? null : new WalkYield(yieldToUser);
            var chain = Chain.Create()
                .ConditionalThen(_ => Player.DistanceTo(destination) > distance, () =>
                    Chain.Create()
                        .Then(new PathfindAndMoveToChain(vnav, destination, source))
                        .Then(new TaskManagerTask(() => WaitUntilArrived(vnav, guard, walk, destination, distance, source), "ChainHelper.WaitUntilArrived",
                            new TaskManagerConfiguration { TimeLimitMS = ArriveTimeLimitMs }))
                        .Then(_ => vnav.Stop())
                );

            return onFinished == null ? chain : chain.Then(_ => onFinished(guard));
        };
    }

    // 一段走路的讓位狀態；只在 framework 執行緒用。
    private sealed class WalkYield(Func<bool> enabled)
    {
        public readonly Func<bool> Enabled = enabled;

        public readonly ArrivalYieldGate Gate = new();

        public readonly HandsOnProbe HandsOn = new();

        public bool Reported;
    }

    private static bool WaitUntilArrived(VNavmesh vnav, ArrivalGuard guard, WalkYield? walk, Vector3 destination, float distance, TravelSource source)
    {
        var now = Environment.TickCount64;
        var input = false;
        if (walk != null)
        {
            // 按鍵每拍都讀：短按可能落在兩次取樣之間。
            walk.Gate.Enabled = walk.Enabled();
            input = walk.Gate.Enabled && walk.HandsOn.ReadInput(now);
        }

        var player = Svc.Objects.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        var left = Vector3.Distance(player.Position, destination);
        if (left > distance && !guard.ShouldPoll(now))
        {
            return false;
        }

        // 戰鬥中停下不算「停下」、不重走：與趕路監看的 PauseForCombat 一致，戰鬥結束後再判斷。
        var navigating = vnav.IsRunning() || vnav.IsSimpleMoveInProgress();
        var busy = navigating || Svc.Condition[ConditionFlag.InCombat];
        if (walk is { Gate.Enabled: true } && left > distance)
        {
            switch (ObserveWalkYield(walk, player.Position, navigating, input, destination, source, now))
            {
                case ArrivalYieldDecision.Hold:
                    guard.Next(left, distance, true, now);
                    return false;

                case ArrivalYieldDecision.Resume:
                    guard.Next(left, distance, true, now);
                    Svc.Log.Debug($"[BOCCHI][尋路讓位] 走向 {destination:F1}（{source}）：你停手、角色已靜止，從目前位置接續（還差 {left:F1} 米，不算重走）");
                    TravelIntent.Register(destination, source);
                    vnav.PathfindAndMoveTo(destination, false);
                    return false;
            }
        }

        switch (guard.Next(left, distance, busy, now))
        {
            case ArrivalDecision.Arrived:
                return true;

            case ArrivalDecision.Rewalk:
                Svc.Log.Debug($"[BOCCHI][尋路] 走向 {destination:F1}（{source}）停下時還差 {left:F1} 米（要 {distance:F1} 以內）：第 {guard.Rewalks} 次重走");
                TravelIntent.Register(destination, source);
                vnav.PathfindAndMoveTo(destination, false);
                return false;

            case ArrivalDecision.GiveUp:
                Svc.Log.Information($"[BOCCHI][尋路] 走向 {destination:F1}（{source}）重走 {guard.Rewalks} 次仍停在 {left:F1} 米外（要 {distance:F1} 以內）：不再重走，照原流程往下");
                return true;

            default:
                return false;
        }
    }

    private static ArrivalYieldDecision ObserveWalkYield(WalkYield walk, Vector3 player, bool navigating, bool input, Vector3 destination, TravelSource source, long now)
    {
        if (!walk.Gate.ShouldSample(now))
        {
            return walk.Gate.Next(navigating);
        }

        var interacting = !input && HandsOnProbe.Interacting(now);
        var step = walk.Gate.Observe(player, navigating, !navigating && Activity.IsMovementPaused(), input || interacting, now);
        if (walk.Gate.Manual.Yielding)
        {
            HandsOnRecent.Note(now, "走路段正在讓位給你操作");
        }

        var what = step.Event switch
        {
            TravelYieldEvent.Touched => input ? "按了移動鍵" : "在互動：開寶箱、對話一類",
            TravelYieldEvent.Started => $"角色以 {step.Speed:F1} 米/秒持續移動 {ManualMoveDetector.ConfirmMs / 1000f:F1} 秒",
            _ => null,
        };
        if (what != null && !walk.Reported)
        {
            walk.Reported = true;
            Svc.Log.Information($"[BOCCHI][尋路讓位] 走向 {destination:F1}（{source}）途中偵測到你在操作（{what}）：不重走、不算重走次數；"
                                + $"沒有按鍵、沒在互動、角色靜止 {ManualMoveDetector.ResumeQuietMs / 1000f:F1} 秒後從目前位置接續；一次讓位上限 {ManualMoveDetector.MaxYieldMs / 1000} 秒，"
                                + $"按鍵／互動讓位這一段累計上限 {ManualMoveDetector.TouchMaxMs / 1000} 秒，到上限後照原本的重走規則。角色位置 {player:F1}");
        }
        else if (step.Event is TravelYieldEvent.Expired or TravelYieldEvent.TouchLimit)
        {
            Svc.Log.Information($"[BOCCHI][尋路讓位] 走向 {destination:F1}（{source}）"
                                + $"{(step.Event == TravelYieldEvent.Expired ? $"讓位超過 {ManualMoveDetector.MaxYieldMs / 1000} 秒" : $"為按鍵／互動讓位累計超過 {ManualMoveDetector.TouchMaxMs / 1000} 秒")}："
                                + $"這一段之後照原本的重走規則。角色位置 {player:F1}");
        }

        return walk.Gate.Next(navigating);
    }

    public static Func<Chain> MoveToAndWait(Vector3 destination, float distance)
    {
        var vnav = IPC.GetSubscriber<VNavmesh>();
        return () => Chain.Create()
            .ConditionalThen(_ => Player.DistanceTo(destination) > distance, () =>
                Chain.Create()
                    .Then(_ => vnav.FollowPath([destination], false))
                    .WaitUntilNear(vnav, destination, distance)
                    .Then(_ => vnav.Stop())
            );
    }

    public static TreasureSightChain TreasureSightChain()
    {
        return new TreasureSightChain(Modules.GetModule<TreasureModule>());
    }
}
