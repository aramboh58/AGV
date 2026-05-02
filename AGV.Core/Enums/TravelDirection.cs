using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.Enums
{
    /// <summary>
    /// Defines the physical travel direction of a vehicle on a move.
    /// Speed is always a positive magnitude — direction is expressed here.
    /// </summary>
    public enum TravelDirection : byte
    {
        Forward = 1,
        Reverse = 2
    }
}