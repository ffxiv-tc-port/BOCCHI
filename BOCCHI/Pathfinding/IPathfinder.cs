using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace BOCCHI.Pathfinding;

public interface IPathfinder
{
    PathfinderState State { get; }

    Task<List<PathfinderStep>> FindPath(Vector3 start, List<uint> nodes);

    /// <summary><paramref name="priority"/> 裡的點排在最前面（彼此之間照近似最短路），其餘接在後面；沒實作的退回不分先後。</summary>
    Task<List<PathfinderStep>> FindPath(Vector3 start, List<uint> nodes, IReadOnlyCollection<uint> priority)
    {
        return FindPath(start, nodes);
    }

    /// <summary>同三個參數的版本，第一段照 <paramref name="startLeg"/> 的步行路長比較走法；沒實作的退回三個參數的版本。</summary>
    Task<List<PathfinderStep>> FindPath(Vector3 start, List<uint> nodes, IReadOnlyCollection<uint> priority, StartLegCosts startLeg)
    {
        return FindPath(start, nodes, priority);
    }
}
