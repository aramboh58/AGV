using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Topology.Services
{
    /// <summary>
    /// Holds the current active RoadMapGraph instance.
    /// Updated atomically when TopologyVersionManager activates
    /// a new topology version.
    /// </summary>
    public sealed class RoadMapGraphHolder
    {
        private volatile RoadMapGraph? _graph;

        public RoadMapGraph? Graph => _graph;
        public bool IsReady => _graph is not null;

        public void Update(RoadMapGraph graph)
        {
            _graph = graph;
        }

        public RoadMapGraph GetRequired()
            => _graph ?? throw new InvalidOperationException(
                "RoadMapGraph is not yet loaded. " +
                "Ensure TopologyBackgroundService has completed " +
                "its initial load before accessing the road map.");
    }
}
