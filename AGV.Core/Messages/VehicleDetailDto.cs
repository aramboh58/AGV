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
    List<decimal> SocHistory);
}
