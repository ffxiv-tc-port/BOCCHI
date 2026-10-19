using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BOCCHI.Automation;
using BOCCHI.Data;
using BOCCHI.Modules.MobFarmer.States;
using ECommons.GameHelpers;
using ECommons.Reflection;
using Ocelot.Modules;
using Ocelot.States;
using Ocelot.Windows;

namespace BOCCHI.Modules.MobFarmer;

public class Farmer : IDisposable
{
    /// <summary>拉怪路徑用的動作鏈佇列名稱；停止時要一起中止。</summary>
    public const string QueueName = "MobFarmer+Farmer";

    public bool Running { get; private set; } = false;

    public Vector3 StartingPoint { get; private set; } = Vector3.Zero;

    public IRotationPlugin RotationPlugin { get; private set; } = new BlankRotationPlugin();

    /// <summary>拉怪與打怪改寫目標的入口；兩個階段共用，才分得出哪一隻是 BOCCHI 自己選的。</summary>
    public readonly TargetKeeper Targets = new() { Source = "自動打怪" };

    private readonly MobFarmerModule module;

    private readonly Dictionary<string, Func<IModule, IRotationPlugin>> rotationPlugins = new()
    {
        { "WrathCombo", m => new Wrath(m) },
    };

    public readonly StateMachine<FarmerPhase, MobFarmerModule> StateMachine;

    public Farmer(MobFarmerModule module)
    {
        this.module = module;
        StateMachine = new StateMachine<FarmerPhase, MobFarmerModule>(FarmerPhase.Waiting, module);
    }

    public void Update(UpdateContext context)
    {
        if (!context.IsForModule<MobFarmerModule>(out var module))
        {
            return;
        }


        if (!Running)
        {
            return;
        }

        RotationPlugin.Tick();
        if (!module.Scanner.Mobs.Any())
        {
            return;
        }

        StateMachine.Update();
    }

    public void Draw(RenderContext context)
    {
        // 先看設定再看怪：Mobs 是延遲求值的查詢，每次 Any() 都重新走訪物件表（離開新月島後照樣每幀跑），
        // 除錯線沒開時不必付這筆。三個條件都只決定要不要提早返回，順序不影響畫出來的東西。
        if (!Running && !module.Config.ShouldRenderDebugLinesWhileNotRunning)
        {
            return;
        }

        if (!module.Config.RenderDebugLines)
        {
            return;
        }

        if (!module.Scanner.Mobs.Any())
        {
            return;
        }

        foreach (var mob in module.Scanner.NotInCombat)
        {
            var color = new Vector4(0.9f, 0.1f, 0.1f, 1f);
            if (module.Config.Mobs.Contains((Mob)mob.NameId))
            {
                color = new Vector4(0.9f, 0.1f, 0.9f, 1f);
            }

            context.DrawLine(mob.Position, color);
        }

        foreach (var mob in module.Scanner.InCombat)
        {
            context.DrawLine(mob.Position, new Vector4(0.1f, 0.9f, 0.1f, 1f));
        }
    }

    /// <summary>由 AutomationControl 呼叫；循環插件在開始時才挑，WrathCombo 晚載入也用得到。</summary>
    public void Start()
    {
        Running = true;
        StateMachine.Reset();
        Targets.Reset();
        StartingPoint = Player.Position;

        RotationPlugin.Dispose();
        RotationPlugin = CreateRotationPlugin();
        RotationPlugin.PhantomJobOff();
    }

    /// <summary>只重設本身狀態並交還 WrathCombo 的租約（冪等）；動作鏈與導航由 AutomationControl 收。</summary>
    public void ResetRun()
    {
        Running = false;
        StateMachine.Reset();
        RotationPlugin.Release();
    }

    private IRotationPlugin CreateRotationPlugin()
    {
        foreach (var (plugin, factory) in rotationPlugins)
        {
            if (DalamudReflector.TryGetDalamudPlugin(plugin, out _, false, true))
            {
                return factory(module);
            }
        }

        return new BlankRotationPlugin();
    }

    public void Dispose()
    {
        RotationPlugin.Dispose();
    }
}
