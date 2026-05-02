using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.Enums
{
    /// <summary>
    /// VDA 5050 v2.0 order states.
    /// </summary>
    public enum OrderState
    {
        Idle = 0,
        Waiting = 1,
        Running = 2,
        Paused = 3,
        Finished = 4,
        Failed = 5
    }
}