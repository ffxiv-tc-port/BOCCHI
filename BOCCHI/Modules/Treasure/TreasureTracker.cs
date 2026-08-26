using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BOCCHI.Data;
using BOCCHI.Enums;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BOCCHI.Modules.Treasure;

public class TreasureTracker : IDisposable
{
    public List<Treasure> Treasures { get; private set; } = [];

    public bool CountInitialised { get; private set; } = false;

    public int BronzeChests { get; private set; } = 0;

    public int SilverChests { get; private set; } = 0;

    private readonly TimeSpan ParseWideTextCooldown = TimeSpan.FromSeconds(5);

    private DateTime LastParseWideText = DateTime.MinValue;

    public TreasureTracker()
    {
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "_WideText", OnWideTextPostDraw);
    }

    public void Tick(Plugin plugin)
    {
        // 用 GameObjectId 當鍵,不要用 DataId:
        // (1) 同型寶箱共用 DataId,ToDictionary 遇到重複鍵會丟 ArgumentException,
        //     而這裡每幀都跑 → 整個 Treasure 模組會靜默失效。
        // (2) 用 DataId 當鍵時,已消失寶箱的陳舊項目會被「同型的另一個寶箱」保住,
        //     接著 CheckOpened() 就去解參考已釋放的位址。
        var treasures = Svc.Objects
            .Where(o => o is { ObjectKind: ObjectKind.Treasure })
            .ToDictionary(o => o.GameObjectId, o => o);

        var knownIds = Treasures.Select(t => t.Id).ToHashSet();

        // Removed
        for (var i = Treasures.Count - 1; i >= 0; i--)
        {
            var treasure = Treasures[i];
            if (!treasures.ContainsKey(treasure.Id) || !treasure.IsValid())
            {
                Treasures.RemoveAt(i);
            }
        }

        // Added
        foreach (var (objectId, obj) in treasures)
        {
            if (knownIds.Contains(objectId))
            {
                continue;
            }

            var treasure = new Treasure(obj);
            if (treasure.IsValid())
            {
                Treasures.Add(treasure);
            }
        }

        Treasures = Treasures.OrderBy(t => Player.DistanceTo(t.GetPosition())).ToList();

        foreach (var treasure in Treasures)
        {
            if (treasure.CheckOpened())
            {
                if (treasure.GetTreasureType() == TreasureType.Bronze)
                {
                    BronzeChests = Math.Max(0, BronzeChests - 1);
                }
                else if (treasure.GetTreasureType() == TreasureType.Silver)
                {
                    SilverChests = Math.Max(0, SilverChests - 1);
                }
            }
        }
    }

    private unsafe void OnWideTextPostDraw(AddonEvent type, AddonArgs args)
    {
        if (!ZoneData.IsInOccultCrescent())
        {
            return;
        }

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || !addon->IsVisible)
        {
            return;
        }

        var timeSinceLast = DateTime.Now - LastParseWideText;
        if (timeSinceLast < ParseWideTextCooldown)
        {
            return;
        }

        LastParseWideText = DateTime.Now;

        var pattern = LogMessageHelper.GetLogMessagePattern(10965);

        // 🔴 兩層都可為 null：GetNodeById 找不到 id 3 的節點時回 null，
        // 找到的節點不是文字節點時 GetAsAtkTextNode() 也回 null。
        // 任一層沒過就跳過本次解析(PostDraw 事件路徑，不寫 log)。
        var node = addon->GetNodeById(3);
        if (node == null)
        {
            return;
        }

        var textNode = node->GetAsAtkTextNode();
        if (textNode == null)
        {
            return;
        }

        var text = textNode->NodeText.ToString();
        var match = Regex.Match(text, pattern);

        if (!match.Success)
        {
            return;
        }

        SilverChests = int.Parse(match.Groups[1].Value);
        BronzeChests = int.Parse(match.Groups[2].Value);
        CountInitialised = true;
    }

    public void Dispose()
    {
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostDraw, "_WideText", OnWideTextPostDraw);
    }
}
