using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.Enums
{
    /// <summary>
    /// VDA 5050 v2.0 operating modes.
    /// </summary>
    public enum OperatingMode
    {
        Automatic = 0,
        SemiAutomatic = 1,
        Manual = 2,
        Service = 3,
        Teaching = 4
    }
}