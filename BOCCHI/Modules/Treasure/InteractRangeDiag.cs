using System;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace BOCCHI.Modules.Treasure;

/// <summary>
/// 寶箱開箱距離閘的對照診斷：手寫常數（直線 ≤ 2 米）對遊戲 <c>EventFramework.CheckInteractRange</c>。
/// </summary>
/// <remarks>
/// 只記錄，不影響要不要互動；只在框架執行緒、拿到 <paramref name="target"/> 的這一幀呼叫，不保存物件或指標。
/// 每次載入第一次問得到遊戲答案時記一行（一致也記，當校準錨）；之後只記不一致，同一種 30 秒一行。格式同 AutoRetainer 的傳喚鈴對照。
/// </remarks>
internal static unsafe class InteractRangeDiag
{
    private const string Tag = "[BOCCHI][互動距離]";

    private const long MismatchIntervalMs = 30_000;

    private static bool anchorLogged;

    private static bool unavailableLogged;

    private static int compared;

    private static int mismatches;

    private static (bool Constant, bool Game) lastMismatchKey;

    private static long lastMismatchAt;

    /// <param name="distance">現行判定用的距離（Player.DistanceTo，直線）。</param>
    /// <param name="limit">現行常數；<paramref name="distance"/> 不超過它＝現行判定搆得到。</param>
    internal static void CompareChest(IGameObject target, float distance, float limit)
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null || player.Address == nint.Zero || target.Address == nint.Zero)
        {
            return;
        }

        var game = AskGame(player, target);
        if (game == null)
        {
            if (!unavailableLogged)
            {
                unavailableLogged = true;
                Svc.Log.Information($"{Tag} 問不到遊戲的 CheckInteractRange（函式沒解析到或 EventFramework 還沒建立），這次不對照；之後問得到會再記");
            }

            return;
        }

        compared++;
        var constant = distance <= limit;
        var agree = constant == game.Value;
        if (agree && anchorLogged)
        {
            return;
        }

        var now = Environment.TickCount64;
        var key = (constant, game.Value);
        if (!agree)
        {
            mismatches++;
            if (anchorLogged && key == lastMismatchKey && now - lastMismatchAt < MismatchIntervalMs)
            {
                return;
            }

            lastMismatchKey = key;
            lastMismatchAt = now;
        }

        var head = anchorLogged ? "寶箱開箱距離判定不一致" : (agree ? "首次對照（一致）" : "首次對照（不一致）");
        anchorLogged = true;
        var p = player.Position;
        var t = target.Position;
        var horizontal = MathF.Sqrt((t.X - p.X) * (t.X - p.X) + (t.Z - p.Z) * (t.Z - p.Z));
        Svc.Log.Information($"{Tag} {head}：常數={(constant ? "搆得到" : "搆不到")}（直線 {distance:F2} 米，門檻 {limit:F2} 米），遊戲 CheckInteractRange={(game.Value ? "搆得到" : "搆不到")}；"
                            + $"水平 {horizontal:F2} 米，高低差 {t.Y - p.Y:+0.00;-0.00} 米，寶箱 {target.ObjectKind} 0x{target.BaseId:X}，區域 {Svc.ClientState.TerritoryType}，"
                            + $"本次載入對照 {compared} 次、不一致 {mismatches} 次（開箱步驟每 250 毫秒算一次）");
    }

    /// <returns><see langword="null"/>＝問不到（函式沒解析到，或單例還沒建立）。</returns>
    private static bool? AskGame(IGameObject player, IGameObject target)
    {
        if (EventFramework.MemberFunctionPointers.CheckInteractRange is null)
        {
            return null;
        }

        try
        {
            var framework = EventFramework.Instance();
            if (framework == null)
            {
                return null;
            }

            return framework->CheckInteractRange((GameObjectStruct*)player.Address, (GameObjectStruct*)target.Address, 1, false);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
