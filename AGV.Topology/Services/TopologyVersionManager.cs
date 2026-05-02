using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Topology.Services
{
    /// <summary>
    /// Tracks roadmap versions and maintains the load audit trail.
    ///
    /// The topology version system ensures the host always knows
    /// exactly which version of the road network is active, when it
    /// was loaded, and by whom. This is critical for:
    ///
    ///   — Correlating forensic logs with the topology that was
    ///     active when an incident occurred
    ///   — Auditing who deployed a topology change and when
    ///   — Providing a rollback reference if a new version
    ///     causes operational problems
    ///
    /// Version lifecycle:
    ///   1. Road system engineer prepares new topology in tooling
    ///   2. New RoadmapVersion record created in database
    ///   3. TopologyVersionManager detects new version
    ///   4. New RoadMapGraph built from database
    ///   5. Atomic swap — new graph replaces old
    ///   6. Load audit record written
    ///   7. All services notified via topology changed event
    /// </summary>
    public sealed class TopologyVersionManager
    {
        private volatile RoadmapVersionInfo? _activeVersion;
        private readonly List<RoadmapVersionInfo> _versionHistory = new();
        private readonly object _historyLock = new();

        /// <summary>
        /// The currently active roadmap version.
        /// Null if no topology has been loaded yet.
        /// </summary>
        public RoadmapVersionInfo? ActiveVersion => _activeVersion;

        /// <summary>
        /// True if a topology has been successfully loaded.
        /// </summary>
        public bool IsLoaded => _activeVersion is not null;

        /// <summary>
        /// Raised when a new topology version is activated.
        /// Subscribers (routing engine, fleet manager, traffic manager)
        /// must invalidate any cached topology-dependent state.
        /// </summary>
        public event EventHandler<TopologyVersionChangedEventArgs>?
            TopologyVersionChanged;

        /// <summary>
        /// Activates a new roadmap version.
        /// Records the load in the audit trail and raises the
        /// TopologyVersionChanged event.
        /// </summary>
        public void ActivateVersion(
            int versionId,
            string versionLabel,
            string loadedByUser,
            RoadMapGraph graph)
        {
            var previous = _activeVersion;

            var newVersion = new RoadmapVersionInfo
            {
                VersionId = versionId,
                VersionLabel = versionLabel,
                LoadedByUser = loadedByUser,
                LoadedAt = DateTime.UtcNow,
                NodeCount = graph.Nodes.Count,
                MoveCount = graph.Moves.Count,
                AreaCount = graph.Areas.Count,
            };

            // Atomic swap
            _activeVersion = newVersion;

            // Append to history
            lock (_historyLock)
            {
                _versionHistory.Add(newVersion);
            }

            // Notify all subscribers
            TopologyVersionChanged?.Invoke(this,
                new TopologyVersionChangedEventArgs
                {
                    NewVersion = newVersion,
                    PreviousVersion = previous,
                    Graph = graph
                });
        }

        /// <summary>
        /// Returns the full version load history for this session.
        /// Ordered oldest to newest.
        /// </summary>
        public IReadOnlyList<RoadmapVersionInfo> GetVersionHistory()
        {
            lock (_historyLock)
            {
                return _versionHistory.AsReadOnly();
            }
        }

        /// <summary>
        /// Returns true if the specified version ID is newer than
        /// the currently active version.
        /// Used to detect when a topology update is available.
        /// </summary>
        public bool IsNewerVersionAvailable(int candidateVersionId)
            => _activeVersion is null
            || candidateVersionId > _activeVersion.VersionId;
    }

    /// <summary>
    /// Metadata about a loaded roadmap version.
    /// </summary>
    public sealed class RoadmapVersionInfo
    {
        public int VersionId { get; init; }
        public string VersionLabel { get; init; } = string.Empty;
        public string LoadedByUser { get; init; } = string.Empty;
        public DateTime LoadedAt { get; init; }
        public int NodeCount { get; init; }
        public int MoveCount { get; init; }
        public int AreaCount { get; init; }

        public override string ToString()
            => $"v{VersionId} '{VersionLabel}' " +
               $"loaded {LoadedAt:HH:mm:ss} UTC by {LoadedByUser} " +
               $"({NodeCount} nodes, {MoveCount} moves, {AreaCount} areas)";
    }

    /// <summary>
    /// Event arguments for topology version change notifications.
    /// </summary>
    public sealed class TopologyVersionChangedEventArgs : EventArgs
    {
        public RoadmapVersionInfo NewVersion { get; init; } = null!;
        public RoadmapVersionInfo? PreviousVersion { get; init; }
        public RoadMapGraph Graph { get; init; } = null!;
        public bool IsInitialLoad => PreviousVersion is null;
    }
}