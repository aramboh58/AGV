using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.Enums
{
    /// <summary>
    /// Type of load carried on a mission.
    /// </summary>
    public enum LoadType
    {
        None = 0,
        Roll = 1,
        WasteBin = 2,
        Pallet = 3,
        Cart = 4
    }
}