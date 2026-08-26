using System;
using System.Collections.Generic;
using BOCCHI.Data;
using BOCCHI.Enums;
using ECommons.DalamudServices;
using Ocelot.IPC;
using Ocelot.Modules;

namespace BOCCHI.Modules.MobFarmer;

public class Wrath : IRotationPlugin
{
    private WrathCombo wrath;

    private Guid lease;

    private Dictionary<JobId, string> WrathOptions = new()
    {
        { JobId.Cannoneer, "Phantom_Cannoneer" },
    };

    public Wrath(IModule module)
    {
        wrath = module.GetIPCSubscriber<WrathCombo>();
        var lease = wrath.RegisterForLease(Svc.PluginInterface.InternalName, module.GetType().FullName!);
        if (lease == null)
        {
            throw new Exception("Unable to create Wrath Combo");
        }

        this.lease = (Guid)lease;
    }

    public void PhantomJobOn(Job? job = null)
    {
        job ??= Job.Current;

        if (!WrathOptions.TryGetValue(job.id, out var option))
        {
            return;
        }

        wrath.SetComboOptionState(lease, option.ToString(), true);
    }

    public void PhantomJobOff(Job? job = null)
    {
        job ??= Job.Current;

        if (!WrathOptions.TryGetValue(job.id, out var option))
        {
            return;
        }

        wrath.SetComboOptionState(lease, option, false);
    }

    void IDisposable.Dispose()
    {
        // Plugin teardown order is not guaranteed, so WrathCombo may already be
        // gone by the time we get here - ReleaseControl then throws
        // IpcNotReadyError. Ocelot disposes its modules with List.ForEach, so
        // letting that escape aborts disposal of every module queued after this
        // one. Observed live on TC 2026-07-29, on every game exit:
        //   [ERR] [LOCALPLUGIN] BOCCHI(...): 处置 instance 失败
        //   IpcNotReadyError: IPC method WrathCombo.ReleaseControl was not registered yet
        // Nothing useful can be done about a lease held by a plugin that no
        // longer exists, so log it and keep unwinding.
        try
        {
            wrath.ReleaseControl(lease);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning("[Wrath] ReleaseControl failed during dispose "
                            + $"(WrathCombo most likely already unloaded): {ex.Message}");
        }
    }
}
