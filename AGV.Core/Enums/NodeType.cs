using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.Enums
{
    /// <summary>
    /// Defines the traversal behavior of a node on the road network.
    /// </summary>
    public enum NodeType : byte
    {
        /// <summary>
        /// Vehicle may stop on this node during route execution.
        /// </summary>
        StopNode = 1,

        /// <summary>
        /// Node is on the transit path but vehicles may not stop here.
        /// </summary>
        NoStopNode = 2,

        /// <summary>
        /// Node has moves in/out but cannot be used as a through-routing
        /// node when calculating shortest/least cost paths.
        /// </summary>
        DestinationOnly = 3
    }
}