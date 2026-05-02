using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Entities;
using AGV.Core.Messages;

namespace AGV.Core.Interfaces
{
    /// <summary>
    /// The site-specific customization API — the strongly typed
    /// boundary between the generic AGV host platform and
    /// customer/site-specific business logic.
    ///
    /// This interface is the evolved successor to the APlus p-code,
    /// JScript, and C# DLL customization layers used in JBT systems.
    /// It is strongly typed, AI-navigable, and fully testable —
    /// properties the old scripting layers never had.
    ///
    /// How it works:
    ///   — AGV.Core defines this interface (the contract)
    ///   — Site-specific assemblies implement it:
    ///       NYT.AGV.Simulation implements NYT-specific logic
    ///       A future AGV.NYT.Site assembly implements production logic
    ///       AGV.Hospital.Site, AGV.PnG.Site etc. for other customers
    ///   — The host registers the implementation via DI in AGV.Host
    ///   — All hook methods have default no-op implementations so the
    ///     host runs correctly with no customization installed
    ///
    /// AI generation note:
    ///   This interface is designed to be a clean, well-documented
    ///   target for AI-assisted code generation. A site engineer can
    ///   describe business rules in plain language and an AI assistant
    ///   can generate the implementing C# against this contract.
    ///   The engineer reviews and approves — domain knowledge drives
    ///   the specification, AI handles the implementation syntax.
    ///
    /// Default implementations return neutral/pass-through values
    /// so the host operates correctly without any customization.
    /// </summary>
    public interface ICustomizationApi
    {
        // ----------------------------------------------------------------
        // Mission lifecycle hooks
        // ----------------------------------------------------------------

        /// <summary>
        /// Called when a new mission is created and enqueued.
        /// Site logic may modify priority, add source system references,
        /// or validate business rules before the mission enters dispatch.
        ///
        /// Return the (possibly modified) MissionContext.
        /// Default: return context unchanged.
        /// </summary>
        Task<MissionContext> OnMissionCreatedAsync(
            MissionContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(context);

        /// <summary>
        /// Called when a vehicle arrives at a pickup node, before the
        /// pick action fires. Site logic may:
        ///   — Validate load identity at the pickup
        ///   — Determine if a mission swap is needed (P&G pattern)
        ///   — Interface with external systems (conveyor PLC, WMS)
        ///
        /// Return true to allow pickup to proceed.
        /// Return false to hold the vehicle (host issues waitForTrigger).
        /// Default: return true.
        /// </summary>
        Task<bool> OnVehicleArrivedAtPickupAsync(
            int vehicleId,
            MissionContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        /// <summary>
        /// Called when a swap candidate is detected — two vehicles
        /// have arrived at sibling pickup nodes in mismatched order.
        ///
        /// Return true to approve the swap.
        /// Return false to override and proceed with original assignments.
        /// Default: return true (always approve detected swaps).
        /// </summary>
        Task<bool> OnSwapCandidateDetectedAsync(
            MissionSwap proposedSwap,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        /// <summary>
        /// Called when a mission completes successfully.
        /// Site logic may notify external systems (SAP, WMS, MES)
        /// of the completion and confirm storage strategy compliance.
        /// Default: no-op.
        /// </summary>
        Task OnMissionCompletedAsync(
            MissionContext context,
            int completingVehicleId,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        /// <summary>
        /// Called when a mission faults and is being transferred.
        /// Site logic may notify external systems of the delay
        /// or adjust downstream scheduling.
        /// Default: no-op.
        /// </summary>
        Task OnMissionFaultedAsync(
            MissionTransfer transfer,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        // ----------------------------------------------------------------
        // Drop destination resolution
        // ----------------------------------------------------------------

        /// <summary>
        /// Resolves the drop destination node for a mission given the
        /// load identity confirmed at pickup.
        ///
        /// This is the hook for pre-assigned drop destination systems
        /// (P&G SAP pattern, WMS rack assignment, etc.) where the
        /// drop destination is determined by an external system based
        /// on the actual load identity confirmed at pickup.
        ///
        /// Return the resolved drop NodeId, or null to use the
        /// pre-assigned drop destination from the MissionContext.
        /// Default: return null (use pre-assigned destination).
        /// </summary>
        Task<int?> ResolveDropDestinationAsync(
            MissionContext context,
            string? confirmedLoadIdentity,
            CancellationToken cancellationToken = default)
            => Task.FromResult<int?>(null);

        // ----------------------------------------------------------------
        // Vehicle and zone hooks
        // ----------------------------------------------------------------

        /// <summary>
        /// Called when a vehicle enters an area.
        /// Site logic may trigger zone rezoning, update external
        /// dashboards, or enforce application-specific area rules.
        /// Default: no-op.
        /// </summary>
        Task OnVehicleEnteredAreaAsync(
            AreaTransitEvent transitEvent,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        /// <summary>
        /// Called when a vehicle exits an area.
        /// Default: no-op.
        /// </summary>
        Task OnVehicleExitedAreaAsync(
            AreaTransitEvent transitEvent,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        /// <summary>
        /// Called when the fleet manager evaluates zone staffing.
        /// Site logic may override the default zone assignment for
        /// a specific vehicle based on application rules.
        ///
        /// Example: hospital kitchen zone — when a meal run begins,
        /// return the kitchen zone ID to redirect this vehicle.
        /// Return null to use default zone assignment logic.
        /// Default: return null.
        /// </summary>
        Task<int?> ResolveVehicleZoneAsync(
            Vehicle vehicle,
            CancellationToken cancellationToken = default)
            => Task.FromResult<int?>(null);

        // ----------------------------------------------------------------
        // Aplus/JScript check equivalents
        // ----------------------------------------------------------------

        /// <summary>
        /// Evaluates a named script check — the equivalent of the
        /// Aplus/JScript check type from the NYT check table.
        ///
        /// The host calls this when it encounters a check record with
        /// CheckType = Aplus/Script. The macroName corresponds to the
        /// APlusCheckMacro field in the check table (e.g.
        /// "APLCenterToLoadCheck", "APLHomeToCenterCheck").
        ///
        /// Return TrafficClearance.Granted to allow traversal.
        /// Return TrafficClearance.Hold to issue waitForTrigger.
        /// Return TrafficClearance.Denied to block permanently.
        ///
        /// Default: return Granted (pass-through for unknown macros).
        /// </summary>
        Task<TrafficClearance> EvaluateScriptCheckAsync(
            string macroName,
            int vehicleId,
            int fromNodeId,
            int toNodeId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(TrafficClearance.Granted);
    }
}