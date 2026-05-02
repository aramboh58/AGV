using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.Enums
{
    /// <summary>
    /// Reason a node or move has been blocked at runtime.
    /// </summary>
    public enum BlockReason
    {
        Unknown = 0,
        EquipmentOutage = 1,
        Maintenance = 2,
        TrafficBottleneck = 3,
        EmergencyStop = 4,
        ManualOverride = 5
    }
}