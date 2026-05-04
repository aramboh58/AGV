using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Enums;
using AGV.Core.Messages;
using AGV.Core.Logging;
using Microsoft.Extensions.Logging;

namespace AGV.Fleet.Services
{
    /// <summary>
    /// Thread-safe priority mission queue.
    ///
    /// Missions are ordered by:
    ///   1. MissionPriority (Emergency first, ParkOrCharge last)
    ///   2. CreatedAt timestamp within the same priority tier (FIFO)
    ///
    /// The queue is owned exclusively by FleetManagerService —
    /// only the fleet manager enqueues and dequeues missions.
    /// Other services request mission creation via the fleet manager,
    /// never by writing to this queue directly.
    ///
    /// Dead mission re-queue:
    ///   When a vehicle faults and its mission is returned to the queue,
    ///   the mission is re-enqueued with escalated priority to compensate
    ///   for time already lost. The original MissionId is preserved.
    ///
    /// Thread safety:
    ///   All operations use a lock for consistency — the queue is not
    ///   a hot path (dispatch runs at most every few hundred ms) so
    ///   a simple lock is appropriate here over a concurrent structure.
    /// </summary>
    public sealed class MissionQueueService
    {
        private readonly List<MissionContext> _queue = new();
        private readonly object _lock = new();
        private readonly ILogger _logger;

        public MissionQueueService(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger(LogDomains.Dispatch);
        }

        // ----------------------------------------------------------------
        // Enqueue
        // ----------------------------------------------------------------

        /// <summary>
        /// Adds a new mission to the queue.
        /// Inserted in priority order — higher priority missions
        /// are placed ahead of lower priority missions.
        /// Within the same priority, insertion is FIFO by CreatedAt.
        /// </summary>
        public void Enqueue(MissionContext mission)
        {
            lock (_lock)
            {
                // Find insertion point — maintain priority order
                var insertAt = _queue.Count;
                for (int i = 0; i < _queue.Count; i++)
                {
                    if (mission.Priority < _queue[i].Priority)
                    {
                        insertAt = i;
                        break;
                    }
                }
                _queue.Insert(insertAt, mission);

                _logger.LogDebug(
                    "Mission {MissionId} enqueued at position {Position} " +
                    "(priority={Priority}, queue depth={Depth})",
                    mission.MissionId, insertAt,
                    mission.Priority, _queue.Count);
            }
        }

        /// <summary>
        /// Re-enqueues a mission that was returned from a faulted vehicle.
        /// Escalates priority to compensate for time already lost.
        /// Preserves original MissionId and transfer history.
        /// </summary>
        public void RequeueAfterFault(
            MissionContext mission,
            MissionPriority escalatedPriority)
        {
            var escalated = mission.WithEscalatedPriority(escalatedPriority);
            Enqueue(escalated);

            _logger.LogInformation(
                "Mission {MissionId} re-queued after fault " +
                "(original priority={Original}, " +
                "escalated priority={Escalated}, " +
                "transfer count={Transfers})",
                mission.MissionId,
                mission.Priority,
                escalatedPriority,
                mission.TransferHistory.Count);
        }

        // ----------------------------------------------------------------
        // Dequeue
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns and removes the highest priority mission from the queue.
        /// Returns null if the queue is empty.
        /// </summary>
        public MissionContext? Dequeue()
        {
            lock (_lock)
            {
                if (_queue.Count == 0) return null;
                var mission = _queue[0];
                _queue.RemoveAt(0);
                return mission;
            }
        }

        /// <summary>
        /// Returns the highest priority mission without removing it.
        /// Returns null if the queue is empty.
        /// </summary>
        public MissionContext? Peek()
        {
            lock (_lock)
            {
                return _queue.Count > 0 ? _queue[0] : null;
            }
        }

        /// <summary>
        /// Attempts to dequeue a mission for a specific vehicle type.
        /// Used when the fleet manager has only specific vehicle types
        /// available — skips missions that require a different type.
        /// Returns null if no compatible mission is available.
        /// </summary>
        public MissionContext? DequeueForVehicleType(
            Core.Enums.VehicleType vehicleType)
        {
            lock (_lock)
            {
                // For now all missions are compatible with any vehicle
                // of the correct type. Future: load type filtering here.
                return Dequeue();
            }
        }

        // ----------------------------------------------------------------
        // Query
        // ----------------------------------------------------------------

        /// <summary>Current number of pending missions.</summary>
        public int Count
        {
            get { lock (_lock) return _queue.Count; }
        }

        /// <summary>True if the queue is empty.</summary>
        public bool IsEmpty
        {
            get { lock (_lock) return _queue.Count == 0; }
        }

        /// <summary>
        /// Returns a read-only snapshot of the current queue.
        /// Used by the dashboard for mission queue visualization.
        /// </summary>
        public IReadOnlyList<MissionContext> GetSnapshot()
        {
            lock (_lock)
            {
                return _queue.ToList().AsReadOnly();
            }
        }

        /// <summary>
        /// Returns missions in the queue that are approaching their
        /// pickup deadline — used to escalate priority before they expire.
        /// </summary>
        public IReadOnlyList<MissionContext> GetApproachingDeadline(
            TimeSpan warningWindow)
        {
            var threshold = DateTime.UtcNow.Add(warningWindow);
            lock (_lock)
            {
                return _queue
                    .Where(m => m.PickupDeadline.HasValue
                             && m.PickupDeadline.Value <= threshold)
                    .ToList()
                    .AsReadOnly();
            }
        }

        /// <summary>
        /// Removes a specific mission from the queue by MissionId.
        /// Used when a mission is cancelled by the operator.
        /// Returns true if the mission was found and removed.
        /// </summary>
        public bool Cancel(int missionId)
        {
            lock (_lock)
            {
                var idx = _queue.FindIndex(m => m.MissionId == missionId);
                if (idx < 0) return false;
                _queue.RemoveAt(idx);
                _logger.LogInformation(
                    "Mission {MissionId} cancelled from queue",
                    missionId);
                return true;
            }
        }
    }
}
