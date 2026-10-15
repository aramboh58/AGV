using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.Messages
{
    public sealed record VehicleDetailDto(
    int VehicleId,
    string SerialNumber,
    string ActivityState,
    decimal SocPercent,
    bool IsLoaded,
    int? CurrentMissionId,
    List<int> RouteNodeIds,
    int? CurrentNodeId,
    List<decimal> SocHistory,
    MissionPartDto? CurrentPart,
    MissionPartDto? NextPart,
    List<NodeRefDto> LockedNodes,
    List<MoveRefDto> LockedMoves,
    List<NodeRefDto> WaitedNodes);

    /// <summary>
    /// A node id with its display name (null if unnamed). HeldByVehicleId
    /// is set only for nodes a vehicle is waiting on: the vehicle that
    /// currently holds the lock (null if not vehicle-held).
    /// </summary>
    public sealed record NodeRefDto(
        int NodeId, string? Name, int? HeldByVehicleId = null);

    /// <summary>A move (edge) with its endpoint nodes.</summary>
    public sealed record MoveRefDto(
        int MoveId, NodeRefDto From, NodeRefDto To);

    /// <summary>
    /// One part of a mission: the operation ("Pick", "Drop") and
    /// where it happens.
    /// </summary>
    public sealed record MissionPartDto(string Operation, NodeRefDto Location);
}
