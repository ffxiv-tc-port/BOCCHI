using Dalamud.Game.Text;

namespace BOCCHI;

// 玩家打的字會出現的頻道（說話、隊伍、通訊貝、跨服通訊貝、悄悄話、情感動作、/echo）：靠系統訊息文字觸發的判斷一律不收，
// 否則別人把系統訊息原句貼出來也會被當成系統訊息。用排除法不用白名單：台服系統訊息實際走哪個頻道沒驗過。
// ChatGui 給的是原始值：低 7 位元是頻道，高位是來源／對象種類，要先取低 7 位元再比。
public static class PlayerChat
{
    public static bool IsPlayerChannel(XivChatType type)
    {
        var channel = (int)type & 0x7F;
        return channel is >= 10 and <= 37 or >= 101 and <= 107 || channel == (int)XivChatType.Echo;
    }
}
