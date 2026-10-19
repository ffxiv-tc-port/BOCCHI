using System;
using System.Reflection;
using Ocelot.Config.Attributes;
using Ocelot.Config.Handlers;
using Ocelot.Modules;

namespace BOCCHI.Modules.Automator;

/// <summary>標在 <c>HashSet&lt;string&gt;</c> 上：以輔助職業技能清單（勾選＝可以使用）的介面編輯它。</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PhantomActionAllowListAttribute : ConfigAttribute
{
    public override Handler GetHandler(ModuleConfig self, ConfigAttribute attr, PropertyInfo prop)
    {
        return new PhantomActionAllowListHandler(self, attr, prop);
    }
}
