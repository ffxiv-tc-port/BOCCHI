using System;
using System.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.DalamudServices;

namespace BOCCHI.Ledger;

/// <summary>
/// 把聊天訊息轉成可以寫進記錄的文字：玩家連結（PlayerPayload 到連結結尾之間的文字）換成 <see cref="Self"/>／<see cref="Other"/>，
/// 其餘照 TextValue 的規則（只收提供文字的 payload）。只看 payload、不比對字串；不是玩家連結的名字（純文字）不會被遮。
/// 連結沒有結尾時，之後的文字一律不寫（寧可少寫）。
/// </summary>
public static class ChatPlayerMask
{
    public const string Self = "<自己>";

    public const string Other = "<玩家>";

    private static readonly RawPayload LinkTerminator = RawPayload.LinkTerminator;

    /// <summary>寫記錄用：自己的名字當場從 LocalPlayer 讀。只在框架執行緒、要寫診斷時呼叫（會配置）。</summary>
    public static string ForLog(SeString message, out int others)
    {
        return Mask(message, Svc.Objects.LocalPlayer?.Name.TextValue, out others);
    }

    /// <param name="selfName">自己的角色名（不含伺服器）；null 或空字串＝不知道，每個玩家都當別人。</param>
    /// <param name="others">訊息裡別的玩家連結有幾個。</param>
    public static string Mask(SeString message, string? selfName, out int others)
    {
        others = 0;
        var text = new StringBuilder();
        var inPlayerLink = false;
        foreach (var payload in message.Payloads)
        {
            switch (payload)
            {
                case PlayerPayload player:
                    var self = !string.IsNullOrEmpty(selfName) && string.Equals(player.PlayerName, selfName, StringComparison.Ordinal);
                    if (!self)
                    {
                        others++;
                    }

                    text.Append(self ? Self : Other);
                    inPlayerLink = true;
                    break;
                case RawPayload raw when inPlayerLink && raw.Equals(LinkTerminator):
                    inPlayerLink = false;
                    break;
                case ITextProvider provider when !inPlayerLink:
                    text.Append(provider.Text);
                    break;
            }
        }

        return text.ToString();
    }
}
