using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BOCCHI.Enums;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using ExcelAction = Lumina.Excel.Sheets.Action;

namespace BOCCHI.Automation;

/// <summary>向 WrathCombo 要新月島輔助職業的選項清單，配上台服遊戲表裡的職業名與技能名。</summary>
/// <remarks>只在 framework 執行緒使用（設定視窗繪製、AutomationControl 每幀）。</remarks>
public sealed partial class WrathPhantomCatalog(WrathPhantomIpc? ipc)
{
    public sealed record Option(string Name, string Label, string CheckboxId);

    /// <summary>本身不是技能、只負責放行底下幾個技能的中間選項（例：輔助風水士的「天氣」）。</summary>
    public sealed record Gate(string Name, IReadOnlyList<string> Children);

    public sealed record Group(string Root, string JobName, string HeaderId, IReadOnlyList<Option> Options, IReadOnlyList<Gate> Gates);

    // WrathCombo 把新月島的選項掛在這個虛擬職業編號下（它的 OccultCrescent.JobID）。
    private const uint OccultCrescentJobId = 100;

    private const long RetryIntervalMs = 5000;

    // 選項名 → 台服 Action 表的列號，只用來顯示遊戲裡的技能名；不在表上的選項照樣列出，改顯示英文名。
    private static readonly FrozenDictionary<string, uint> ActionIds = new Dictionary<string, uint>
    {
        ["Phantom_Freelancer_OccultResuscitation"] = 41650,
        ["Phantom_Freelancer_OccultTreasuresight"] = 41651,
        ["Phantom_Knight_PhantomGuard"] = 41588,
        ["Phantom_Knight_Pray"] = 41589,
        ["Phantom_Knight_OccultHeal"] = 41590,
        ["Phantom_Knight_Pledge"] = 41591,
        ["Phantom_Monk_PhantomKick"] = 41595,
        ["Phantom_Monk_OccultCounter"] = 41596,
        ["Phantom_Monk_Counterstance"] = 41597,
        ["Phantom_Monk_OccultChakra"] = 41598,
        ["Phantom_Thief_OccultSprint"] = 41646,
        ["Phantom_Thief_Steal"] = 41645,
        ["Phantom_Thief_Vigilance"] = 41647,
        ["Phantom_Thief_TrapDetection"] = 41648,
        ["Phantom_Thief_PilferWeapon"] = 41649,
        ["Phantom_Samurai_Mineuchi"] = 41603,
        ["Phantom_Samurai_Shirahadori"] = 41604,
        ["Phantom_Samurai_Iainuki"] = 41605,
        ["Phantom_Samurai_Zeninage"] = 41606,
        ["Phantom_Berserker_Rage"] = 41592,
        ["Phantom_Berserker_DeadlyBlow"] = 41594,
        ["Phantom_Ranger_PhantomAim"] = 41599,
        ["Phantom_Ranger_OccultFeatherfoot"] = 41600,
        ["Phantom_Ranger_OccultFalcon"] = 41601,
        ["Phantom_Ranger_OccultUnicorn"] = 41602,
        ["Phantom_TimeMage_OccultSlowga"] = 41621,
        ["Phantom_TimeMage_OccultComet"] = 41623,
        ["Phantom_TimeMage_OccultMageMasher"] = 41624,
        ["Phantom_TimeMage_OccultDispel"] = 41622,
        ["Phantom_TimeMage_OccultQuick"] = 41625,
        ["Phantom_Chemist_OccultPotion"] = 41631,
        ["Phantom_Chemist_OccultEther"] = 41633,
        ["Phantom_Chemist_Revive"] = 41634,
        ["Phantom_Chemist_OccultElixir"] = 41635,
        ["Phantom_Bard_MightyMarch"] = 41607,
        ["Phantom_Bard_OffensiveAria"] = 41608,
        ["Phantom_Bard_RomeosBallad"] = 41609,
        ["Phantom_Bard_HerosRime"] = 41610,
        ["Phantom_Oracle_Predict"] = 41636,
        ["Phantom_Oracle_PhantomJudgment"] = 41637,
        ["Phantom_Oracle_Cleansing"] = 41638,
        ["Phantom_Oracle_Blessing"] = 41639,
        ["Phantom_Oracle_Starfall"] = 41640,
        ["Phantom_Oracle_Recuperation"] = 41641,
        ["Phantom_Oracle_PhantomDoom"] = 41642,
        ["Phantom_Oracle_PhantomRejuvenation"] = 41643,
        ["Phantom_Oracle_Invulnerability"] = 41644,
        ["Phantom_Cannoneer_PhantomFire"] = 41626,
        ["Phantom_Cannoneer_HolyCannon"] = 41627,
        ["Phantom_Cannoneer_DarkCannon"] = 41628,
        ["Phantom_Cannoneer_ShockCannon"] = 41629,
        ["Phantom_Cannoneer_SilverCannon"] = 41630,
        ["Phantom_Geomancer_BattleBell"] = 41611,
        ["Phantom_Geomancer_RingingRespite"] = 41619,
        ["Phantom_Geomancer_Suspend"] = 41620,
        ["Phantom_Geomancer_Weather"] = 41612,
        ["Phantom_Geomancer_Sunbath"] = 41613,
        ["Phantom_Geomancer_CloudyCaress"] = 41614,
        ["Phantom_Geomancer_BlessedRain"] = 41615,
        ["Phantom_Geomancer_MistyMirage"] = 41616,
        ["Phantom_Geomancer_HastyMirage"] = 41617,
        ["Phantom_Geomancer_AetherialGain"] = 41618,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    // 技能 → 放行它的中間選項（WrathCombo 的 ParentCombo）；中間選項關著時底下的技能開了也不會用。
    private static readonly FrozenDictionary<string, string> GateOf = new Dictionary<string, string>
    {
        ["Phantom_Geomancer_Sunbath"] = "Phantom_Geomancer_Weather",
        ["Phantom_Geomancer_CloudyCaress"] = "Phantom_Geomancer_Weather",
        ["Phantom_Geomancer_BlessedRain"] = "Phantom_Geomancer_Weather",
        ["Phantom_Geomancer_MistyMirage"] = "Phantom_Geomancer_Weather",
        ["Phantom_Geomancer_HastyMirage"] = "Phantom_Geomancer_Weather",
        ["Phantom_Geomancer_AetherialGain"] = "Phantom_Geomancer_Weather",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenSet<string> GateNames = GateOf.Values.ToFrozenSet(StringComparer.Ordinal);

    private IReadOnlyList<Group> groups = [];

    private Dictionary<string, string> labels = new(StringComparer.Ordinal);

    private long nextUiAttemptAt;

    /// <summary>設定視窗用；還沒拿到清單時最多每 5 秒向 WrathCombo 重問一次。</summary>
    public IReadOnlyList<Group> GetGroups()
    {
        var now = Environment.TickCount64;
        if (groups.Count == 0 && now >= nextUiAttemptAt)
        {
            nextUiAttemptAt = now + RetryIntervalMs;
            Refresh();
        }

        return groups;
    }

    /// <summary>最近一次成功向 WrathCombo 要到的清單；還沒要到時是空的。</summary>
    public IReadOnlyList<Group> Groups
    {
        get => groups;
    }

    /// <summary>是不是目前這份 WrathCombo 清單裡列得出來的選項。</summary>
    public bool Contains(string name)
    {
        return labels.ContainsKey(name);
    }

    public string LabelOf(string name)
    {
        return labels.TryGetValue(name, out var label) ? label : name;
    }

    /// <summary>重新向 WrathCombo 要清單；拿不到時回 false，並保留上一份給設定視窗顯示。</summary>
    public bool Refresh()
    {
        Dictionary<string, List<string>>? byRoot;
        try
        {
            byRoot = ipc?.GetComboOptionNamesForJob(OccultCrescentJobId);
        }
        catch (Exception)
        {
            return false;
        }

        if (byRoot is not { Count: > 0 })
        {
            return false;
        }

        var built = new List<(int Order, Group Group)>();
        var builtLabels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (root, names) in byRoot)
        {
            if (names == null)
            {
                continue;
            }

            var options = new List<Option>();
            var gatesHere = new List<string>();
            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name) || !IsActionOption(name) || builtLabels.ContainsKey(name))
                {
                    continue;
                }

                if (GateNames.Contains(name))
                {
                    gatesHere.Add(name);
                    continue;
                }

                var label = ActionLabel(name);
                builtLabels[name] = label;
                options.Add(new Option(name, label, $"{label}##{name}"));
            }

            if (options.Count == 0)
            {
                continue;
            }

            var gates = gatesHere
                .Select(gate => new Gate(gate, options.Where(o => GateOf.TryGetValue(o.Name, out var g) && g == gate).Select(o => o.Name).ToList()))
                .ToList();
            var (order, jobName) = JobOf(root);
            built.Add((order, new Group(root, jobName, $"###wrath_phantom_{root}", options, gates)));
        }

        groups = built.OrderBy(x => x.Order).ThenBy(x => x.Group.Root, StringComparer.Ordinal).Select(x => x.Group).ToList();
        labels = builtLabels;
        return true;
    }

    // 刻意只管「Phantom_職業_技能」三段式的選項：更深一層的是既有技能的出手條件（例：魔強減速的「等待」），
    // 不是技能本身，一律留給使用者自己的設定。
    private static bool IsActionOption(string name)
    {
        return name.StartsWith("Phantom_", StringComparison.Ordinal) && name.Count(c => c == '_') == 2;
    }

    private static string ActionLabel(string name)
    {
        if (ActionIds.TryGetValue(name, out var id))
        {
            var fromSheet = SheetText(() => Svc.Data.GetExcelSheet<ExcelAction>().GetRowOrDefault(id)?.Name.ExtractText());
            if (!string.IsNullOrWhiteSpace(fromSheet))
            {
                return fromSheet;
            }
        }

        return SplitCamelCase(name[(name.LastIndexOf('_') + 1)..]);
    }

    private static (int Order, string Name) JobOf(string root)
    {
        var part = root.StartsWith("Phantom_", StringComparison.Ordinal) ? root["Phantom_".Length..] : root;
        if (!Enum.TryParse<JobId>(part, out var job) || !Enum.IsDefined(job))
        {
            return (int.MaxValue, SplitCamelCase(part));
        }

        // 這版 Lumina 還沒替 MKDSupportJob 命名欄位；Unknown0 是職業名（與 GameTextTokens 同一個欄位）。
        var fromSheet = SheetText(() => Svc.Data.GetExcelSheet<MKDSupportJob>().GetRowOrDefault((uint)job)?.Unknown0.ExtractText());
        return ((int)job, string.IsNullOrWhiteSpace(fromSheet) ? SplitCamelCase(part) : fromSheet);
    }

    private static string? SheetText(Func<string?> lookup)
    {
        try
        {
            return lookup();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string SplitCamelCase(string value)
    {
        return CamelCaseBoundary().Replace(value, " ");
    }

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CamelCaseBoundary();
}
