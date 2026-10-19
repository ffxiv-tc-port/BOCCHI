using System.Collections.Generic;

namespace BOCCHI.Automation;

/// <summary>輔助職業技能清單與 WrathCombo 使用者設定之間的比對與寫入計畫；只做計算，不碰 IPC。</summary>
public static class PhantomSyncPlan
{
    /// <summary>WrathCombo 自己的設定下這個技能會不會被用：技能、所屬輔助職業、中間選項都開才算開。</summary>
    /// <returns>null＝WrathCombo 沒有回報其中某一項，不知道。</returns>
    public static bool? Effective(IReadOnlyDictionary<string, bool> user, WrathPhantomCatalog.Group group, string option)
    {
        if (!user.TryGetValue(option, out var on) || !user.TryGetValue(group.Root, out var rootOn))
        {
            return null;
        }

        var gate = GateOf(group, option);
        if (gate == null)
        {
            return on && rootOn;
        }

        return user.TryGetValue(gate.Name, out var gateOn) ? on && rootOn && gateOn : null;
    }

    /// <summary>把 touched 裡的技能照清單寫進 WrathCombo，同一個輔助職業的其他技能維持原本的實際效果，要改哪些開關。</summary>
    /// <remarks>輔助職業本身與中間選項只開不關；因此會被連帶放行的其他技能改成關。沒有 touched 的技能時不寫。</remarks>
    /// <returns>false＝WrathCombo 沒有回報這個輔助職業的某一項，整組不寫。</returns>
    public static bool AddWrites(
        WrathPhantomCatalog.Group group,
        IReadOnlySet<string> allowed,
        IReadOnlySet<string> touched,
        IReadOnlyDictionary<string, bool> user,
        Dictionary<string, bool> writes)
    {
        var options = group.Options;
        var anyTouched = false;
        for (var i = 0; i < options.Count && !anyTouched; i++)
        {
            anyTouched = touched.Contains(options[i].Name);
        }

        if (!anyTouched)
        {
            return true;
        }

        var desired = new bool[options.Count];
        var anyDesired = false;
        for (var i = 0; i < options.Count; i++)
        {
            var name = options[i].Name;
            if (Effective(user, group, name) is not { } current)
            {
                return false;
            }

            desired[i] = touched.Contains(name) ? allowed.Contains(name) : current;
            anyDesired |= desired[i];
        }

        var rootOn = user[group.Root];
        var rootAfter = rootOn || anyDesired;
        if (rootAfter && !rootOn)
        {
            writes[group.Root] = true;
        }

        for (var i = 0; i < options.Count; i++)
        {
            var name = options[i].Name;
            var gateAfter = true;
            if (GateOf(group, name) is { } gate)
            {
                var gateOn = user[gate.Name];
                gateAfter = gateOn || AnyDesiredUnder(gate, options, desired);
                if (gateAfter && !gateOn)
                {
                    writes[gate.Name] = true;
                }
            }

            var raw = user[name];
            var target = touched.Contains(name) ? desired[i] : raw && !(rootAfter && gateAfter && !desired[i]);
            if (target != raw)
            {
                writes[name] = target;
            }
        }

        return true;
    }

    private static WrathPhantomCatalog.Gate? GateOf(WrathPhantomCatalog.Group group, string option)
    {
        var gates = group.Gates;
        for (var g = 0; g < gates.Count; g++)
        {
            var children = gates[g].Children;
            for (var c = 0; c < children.Count; c++)
            {
                if (children[c] == option)
                {
                    return gates[g];
                }
            }
        }

        return null;
    }

    private static bool AnyDesiredUnder(WrathPhantomCatalog.Gate gate, IReadOnlyList<WrathPhantomCatalog.Option> options, bool[] desired)
    {
        for (var i = 0; i < options.Count; i++)
        {
            if (!desired[i])
            {
                continue;
            }

            var children = gate.Children;
            for (var c = 0; c < children.Count; c++)
            {
                if (children[c] == options[i].Name)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
