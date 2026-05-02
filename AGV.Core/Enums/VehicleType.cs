using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.Enums
{
    /// <summary>
    /// Physical vehicle classification.
    /// Determines which moves a vehicle may traverse via RoutingType.
    /// </summary>
    public enum VehicleType
    {
        Fork = 1,
        WasteBin = 2
    }
}