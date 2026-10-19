using System;
using BOCCHI.Data;

namespace BOCCHI.Modules.MobFarmer;

public interface IRotationPlugin : IDisposable
{
    public void PhantomJobOn(Job? job = null);

    public void PhantomJobOff(Job? job = null);

    /// <summary>交還對循環插件的一切覆寫（冪等）；之後再呼叫 PhantomJob* 會重新取得。</summary>
    public void Release();

    /// <summary>打怪執行中每幀在 framework 執行緒呼叫；循環插件收回控制後在這裡重新取得。</summary>
    public void Tick();
}
