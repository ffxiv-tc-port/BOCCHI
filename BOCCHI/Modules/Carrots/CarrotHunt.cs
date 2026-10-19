using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BOCCHI.ActionHelpers;
using BOCCHI.Automation;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.ItemHelpers;
using BOCCHI.Pathfinding;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;
using Ocelot.Modules;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace BOCCHI.Modules.Carrots;

public class CarrotHunt(CarrotsModule module) : Hunter(module)
{
    // 「重複」勾選框的標籤：語言換了才重組。
    private string? repeatTemplate;

    private string repeatLabel = string.Empty;

    public override AutomationKind Kind
    {
        get => AutomationKind.CarrotHunt;
    }

    // 同寶箱巡迴：主視窗這一區沒跑過也畫（跑過才出現會像沒有這個功能）；開始／停止在最上方的自動化控制。
    protected override bool HasExtraRows
    {
        get => true;
    }

    protected override void DrawExtraRows(Module<Plugin, Config> owner)
    {
        var config = module.Config;
        var template = module.T("config.repeat_carrot_hunt.label");
        if (!ReferenceEquals(template, repeatTemplate))
        {
            repeatTemplate = template;
            repeatLabel = template + "##carrot-hunt-repeat";
        }

        ImGui.Indent(16);
        var repeat = config.RepeatCarrotHunt;
        if (ImGui.Checkbox(repeatLabel, ref repeat))
        {
            config.RepeatCarrotHunt = repeat;
            module.PluginConfig.Save();
            Svc.Log.Information($"[BOCCHI][胡蘿蔔巡迴] 「重複」改成「{(repeat ? "開" : "關")}」（主視窗）：這一輪走完時照新的設定");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(module.T("config.repeat_carrot_hunt.tooltip"));
        }

        ImGui.Unindent(16);
    }

    protected override IEnumerable<IGameObject> GetValidObjects()
    {
        return Svc.Objects
            .Where(o => o is
            {
                ObjectKind: ObjectKind.EventObj,
                BaseId: (uint)OccultObjectType.Carrot,
                IsDead: false,
            } && o.IsValid());
    }

    protected override Vector3 GetDestinationForCurrentStep()
    {
        return CarrotData.Data.First(c => c.Id == CurrentStep.NodeId).Position;
    }

    protected override IPathfinder CreatePathfinder()
    {
        return new Pathfinder(module.PluginConfig.PathfinderConfig.ReturnCost, module.PluginConfig.PathfinderConfig.TeleportCost);
    }

    protected override Func<Chain> GetInteractionChain(IGameObject obj)
    {
        // 只在拿到 obj 的這一幀抄走位置；物件包裝跨幀會被就地改寫成別的物件或懸空。
        var position = obj.Position;

        // 開黃金財寶箱不詠唱（是一段開箱事件）：交給 BunnyChestOpener 等事件或寶箱消失，互動有節流。
        return () =>
        {
            var chest = new BunnyChestOpener();
            return Chain.Create()
                .BreakIf(() => !GetValidObjects().Any(o => Vector3.Distance(o.Position, position) <= DISTANCE_TO_NODE_TO_USE))
                .ConditionalThen(_ => Player.Mounted, _ => Actions.Unmount.Cast())
                .Wait(500)
                .BreakIf(() => Items.FortuneCarrot.Count() <= 0)
                .Then(_ =>
                {
                    OwnInteraction.Note(Environment.TickCount64);
                    Items.FortuneCarrot.Use();
                })
                .WaitToCast()
                .Then(_ => chest.TryFind(float.MaxValue))
                .Then(_ =>
                {
                    if (!chest.Step(vnav))
                    {
                        return false;
                    }

                    if (chest.Opened)
                    {
                        NoteBunnyChest();
                    }

                    return true;
                });
        };
    }

    protected override List<uint> GetValidNodes(int max)
    {
        return CarrotData.Data.Where(node => node.Level <= max).Select(node => node.Id).ToList();
    }

    protected override void Teardown()
    {
        EndSegment(HuntEnd.Finished, null, module.Config.RepeatCarrotHunt ? "重新開始" : "完成");
        Svc.Log.Information(module.Config.RepeatCarrotHunt
            ? $"[BOCCHI][自動化] 「{AutomationControl.GetName(Kind)}」走完全部 {Steps.Count} 步，設定為重複，從頭再跑一輪。"
            : $"[BOCCHI][自動化] 「{AutomationControl.GetName(Kind)}」走完全部 {Steps.Count} 步，已結束。");

        if (!module.Config.RepeatCarrotHunt)
        {
            stopwatch.Stop();
            running = false;
            Progress.Clear();
        }
        else
        {
            Progress.BeginFresh();
            BeginSegment("重複");
        }

        stepIndex = 0;
        Steps.Clear();
        vnav.Stop();
        Plugin.Chain.Abort();
        StepProcessor.Abort();
        pathfinder = null;
    }
}
