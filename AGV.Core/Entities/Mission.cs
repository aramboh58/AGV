using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Enums;

namespace AGV.Core.Entities
{
    /// <summary>
    /// Represents a single transport mission assigned to a vehicle.
    ///
    /// A Mission is the host-side record of work to be done —
    /// it tracks the full lifecycle from creation through completion
    /// or failure.
    ///
    /// The relationship to VDA 5050:
    ///   — A Mission maps to one or more VDA 5050 Orders sent to the
    ///     vehicle. As the vehicle progresses and the host extends the
    ///     base/horizon window, multiple Order messages are sent for
    ///     a single Mission.
    ///   — The Mission's OrderId is the VDA 5050 orderId that ties
    ///     those Order messages together.
    ///
    /// Pickup and dropoff are expressed as LocationAssignment IDs —
    /// not raw node IDs — so the fleet manager can resolve the correct
    /// node + OperationType + LocationType combination at dispatch time,
    /// including any runtime LocationType overrides.
    /// </summary>
    public class Mission
    {
        /// <summary>
        /// Primary key — surrogate identity for this mission record.
        /// </summary>
        public int MissionId { get; private set; }

        /// <summary>
        /// The VDA 5050 orderId transmitted in Order messages for
        /// this mission. Stable across all base/horizon extension
        /// updates sent during execution.
        /// </summary>
        public string OrderId { get; private set; }

        /// <summary>
        /// The vehicle assigned to execute this mission.
        /// Null until a vehicle is selected by the dispatcher.
        /// </summary>
        public int? AssignedVehicleId { get; private set; }

        /// <summary>
        /// The LocationAssignment from which the load is picked up.
        /// Resolves to a specific Node + OperationType + LocationType.
        /// </summary>
        public int PickupAssignmentId { get; private set; }

        /// <summary>
        /// The LocationAssignment at which the load is dropped off.
        /// </summary>
        public int DropoffAssignmentId { get; private set; }

        /// <summary>
        /// The type of load carried on this mission.
        /// </summary>
        public LoadType LoadType { get; private set; }

        /// <summary>
        /// Current execution state of this mission.
        /// </summary>
        public OrderState State { get; private set; }

        /// <summary>
        /// Dispatch priority — higher values are dispatched first.
        /// Default is 0 (normal priority).
        /// </summary>
        public int Priority { get; private set; }

        /// <summary>
        /// Timestamp when this mission was created by the host.
        /// </summary>
        public DateTime CreatedAt { get; private set; }

        /// <summary>
        /// Timestamp when a vehicle was assigned and execution began.
        /// Null until dispatched.
        /// </summary>
        public DateTime? DispatchedAt { get; private set; }

        /// <summary>
        /// Timestamp when the vehicle arrived at the pickup location.
        /// Null until pickup reached.
        /// </summary>
        public DateTime? PickupArrivedAt { get; private set; }

        /// <summary>
        /// Timestamp when the pickup operation completed and the
        /// vehicle began traveling to dropoff.
        /// </summary>
        public DateTime? PickupCompletedAt { get; private set; }

        /// <summary>
        /// Timestamp when the vehicle arrived at the dropoff location.
        /// </summary>
        public DateTime? DropoffArrivedAt { get; private set; }

        /// <summary>
        /// Timestamp when the mission completed successfully.
        /// </summary>
        public DateTime? CompletedAt { get; private set; }

        /// <summary>
        /// Timestamp when the mission failed.
        /// </summary>
        public DateTime? FailedAt { get; private set; }

        /// <summary>
        /// Reason for failure if the mission failed.
        /// </summary>
        public string? FailureReason { get; private set; }

        // Private constructor for EF Core
        private Mission()
        {
            OrderId = null!;
        }

        public Mission(
            int missionId,
            string orderId,
            int pickupAssignmentId,
            int dropoffAssignmentId,
            LoadType loadType,
            int priority = 0)
        {
            if (missionId <= 0)
                throw new ArgumentOutOfRangeException(nameof(missionId),
                    "MissionId must be a positive integer.");

            if (string.IsNullOrWhiteSpace(orderId))
                throw new ArgumentException(
                    "OrderId cannot be null or empty.", nameof(orderId));

            if (pickupAssignmentId <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(pickupAssignmentId),
                    "PickupAssignmentId must be a positive integer.");

            if (dropoffAssignmentId <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(dropoffAssignmentId),
                    "DropoffAssignmentId must be a positive integer.");

            if (pickupAssignmentId == dropoffAssignmentId)
                throw new ArgumentException(
                    "PickupAssignmentId and DropoffAssignmentId " +
                    "cannot be the same assignment.");

            MissionId = missionId;
            OrderId = orderId;
            PickupAssignmentId = pickupAssignmentId;
            DropoffAssignmentId = dropoffAssignmentId;
            LoadType = loadType;
            Priority = priority;
            State = OrderState.Idle;
            CreatedAt = DateTime.UtcNow;
        }

        // ----------------------------------------------------------------
        // Lifecycle transition methods
        // ----------------------------------------------------------------

        /// <summary>
        /// Assigns a vehicle to this mission and marks it as waiting
        /// for execution to begin.
        /// </summary>
        public void Dispatch(int vehicleId)
        {
            if (vehicleId <= 0)
                throw new ArgumentOutOfRangeException(nameof(vehicleId),
                    "VehicleId must be a positive integer.");

            if (State != OrderState.Idle)
                throw new InvalidOperationException(
                    $"Cannot dispatch mission in state {State}. " +
                    $"Mission must be Idle.");

            AssignedVehicleId = vehicleId;
            State = OrderState.Waiting;
            DispatchedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Marks the mission as actively running — vehicle is en route.
        /// </summary>
        public void Start()
        {
            if (State != OrderState.Waiting)
                throw new InvalidOperationException(
                    $"Cannot start mission in state {State}. " +
                    $"Mission must be Waiting.");
            State = OrderState.Running;
        }

        /// <summary>
        /// Records arrival at the pickup location.
        /// </summary>
        public void RecordPickupArrival()
        {
            PickupArrivedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Records completion of the pickup operation.
        /// </summary>
        public void RecordPickupComplete()
        {
            PickupCompletedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Records arrival at the dropoff location.
        /// </summary>
        public void RecordDropoffArrival()
        {
            DropoffArrivedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Marks the mission as paused.
        /// </summary>
        public void Pause()
        {
            if (State != OrderState.Running)
                throw new InvalidOperationException(
                    $"Cannot pause mission in state {State}.");
            State = OrderState.Paused;
        }

        /// <summary>
        /// Resumes a paused mission.
        /// </summary>
        public void Resume()
        {
            if (State != OrderState.Paused)
                throw new InvalidOperationException(
                    $"Cannot resume mission in state {State}.");
            State = OrderState.Running;
        }

        /// <summary>
        /// Marks the mission as successfully completed.
        /// </summary>
        public void Complete()
        {
            if (State != OrderState.Running)
                throw new InvalidOperationException(
                    $"Cannot complete mission in state {State}.");
            State = OrderState.Finished;
            CompletedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Marks the mission as failed with an optional reason.
        /// </summary>
        public void Fail(string? reason = null)
        {
            State = OrderState.Failed;
            FailedAt = DateTime.UtcNow;
            FailureReason = reason;
        }

        /// <summary>
        /// True if this mission has reached a terminal state
        /// (completed or failed).
        /// </summary>
        public bool IsTerminal
            => State == OrderState.Finished
            || State == OrderState.Failed;

        /// <summary>
        /// Total mission cycle time from dispatch to completion.
        /// Null if the mission has not yet completed.
        /// </summary>
        public TimeSpan? CycleTime
            => CompletedAt.HasValue && DispatchedAt.HasValue
                ? CompletedAt.Value - DispatchedAt.Value
                : null;

        /// <summary>
        /// Time the vehicle spent traveling to pickup and performing
        /// the pickup operation.
        /// Null if pickup has not completed.
        /// </summary>
        public TimeSpan? PickupDuration
            => PickupCompletedAt.HasValue && DispatchedAt.HasValue
                ? PickupCompletedAt.Value - DispatchedAt.Value
                : null;

        public override string ToString()
            => $"Mission[{MissionId}] Order={OrderId} " +
               $"State={State} Load={LoadType} " +
               $"Vehicle={AssignedVehicleId?.ToString() ?? "unassigned"} " +
               $"Priority={Priority}";
    }
}