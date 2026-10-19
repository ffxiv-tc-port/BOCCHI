namespace BOCCHI.Pathfinding;

public enum PathfinderState
{
    None,
    LoadingFile,
    FileLoaded,
    Pathfinding,
    PathfindingDone,

    /// <summary>成本表讀不到（找不到檔、讀檔或解析失敗）；不會再變成 FileLoaded。</summary>
    LoadFailed,
}
