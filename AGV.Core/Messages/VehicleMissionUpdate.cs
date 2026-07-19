using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.Messages
{
    public record VehicleMissionUpdate(
    int VehicleId,
    int? MissionId,
    IReadOnlyList<int> RouteNodeIds);
}
