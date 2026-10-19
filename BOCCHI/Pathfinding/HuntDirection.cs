namespace BOCCHI.Pathfinding;

/// <summary>寶箱巡迴一輪的走法。設定檔存的是數值：既有值不可改，新值加在最後。</summary>
public enum HuntDirection
{
    /// <summary>照規劃的順序走（預設）。</summary>
    Forward = 0,

    /// <summary>已記錄的寶箱照樣先開，其餘的點從正向規劃的另一端開始倒過來走（再照倒過來的方向做局部搜尋）。</summary>
    Reverse = 1,

    /// <summary>每次從頭開始時正向、逆向輪流（插件載入後第一輪正向）。</summary>
    Alternate = 2,
}
