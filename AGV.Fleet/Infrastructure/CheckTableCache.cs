using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Logging;
using Microsoft.Extensions.Logging;

namespace AGV.Fleet.Infrastructure
{
    /// <summary>
    /// In-memory cache of the traffic control check table.
    ///
    /// The check table defines what must be verified before a vehicle
    /// is permitted to advance to the next node on its itinerary.
    /// Each check record specifies an owner (a node, move, or pair)
    /// and a set of conditions that must all pass before the atomic
    /// check+lock operation may proceed.
    ///
    /// Architecture:
    ///   The check table is loaded from SQL at startup into this
    ///   in-memory cache. The real-time traffic management loop
    ///   queries ONLY this cache — never the database.
    ///   SQL is for persistence and audit only.
    ///
    /// NYT check table reference:
    ///   10,677 total checks, 8,373 active (Enabled=1)
    ///   Types: Node(6914), Move(533), NodeWithItin(369),
    ///          Distance(228), Aplus(208), Itinerary(121)
    ///
    /// Cache key:
    ///   (OwnersType, OwnersNumber) — the node or move ID that
    ///   owns the check. Multiple checks may share the same owner.
    ///
    /// Thread safety:
    ///   The cache is read-only after initial load. Targeted
    ///   invalidation replaces individual entries atomically.
    ///   The traffic manager loop reads without locking.
    /// </summary>
    public sealed class CheckTableCache
    {
        private volatile IReadOnlyDictionary<(string OwnersType, int OwnersNumber),
            IReadOnlyList<CheckRecord>> _cache
            = new Dictionary<(string, int), IReadOnlyList<CheckRecord>>();

        private readonly ILogger _logger;

        public CheckTableCache(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger(LogDomains.LockManager);
        }

        // ----------------------------------------------------------------
        // Loading
        // ----------------------------------------------------------------

        /// <summary>
        /// Loads the check table from the provided records.
        /// Called at startup by TopologyService after topology load.
        /// Replaces the entire cache atomically.
        /// </summary>
        public void Load(IEnumerable<CheckRecord> records)
        {
            var grouped = records
                .Where(r => r.Enabled)
                .GroupBy(r => (r.OwnersType, r.OwnersNumber))
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<CheckRecord>)
                         g.OrderBy(r => r.CheckOrder)
                          .ToList()
                          .AsReadOnly());

            _cache = grouped;

            _logger.LogInformation(
                "Check table loaded: {TotalChecks} active checks, " +
                "{UniqueOwners} unique owners",
                grouped.Values.Sum(v => v.Count),
                grouped.Count);
        }

        // ----------------------------------------------------------------
        // Queries
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns all checks applicable to the specified owner.
        /// Returns an empty list if no checks are defined for this owner.
        ///
        /// The traffic manager calls this during Phase 1 (Check+Lock)
        /// to get the checks it must evaluate before locking a node.
        /// </summary>
        public IReadOnlyList<CheckRecord> GetChecks(
            string ownersType, int ownersNumber)
            => _cache.TryGetValue((ownersType, ownersNumber), out var checks)
                ? checks
                : Array.Empty<CheckRecord>();

        /// <summary>
        /// Returns all checks for a node (OwnersType = "Node").
        /// </summary>
        public IReadOnlyList<CheckRecord> GetNodeChecks(int nodeId)
            => GetChecks("Node", nodeId);

        /// <summary>
        /// Returns all checks for a move (OwnersType = "Move").
        /// Move ID encoding from NYT check table:
        /// concatenated from/to node IDs (7-8 digits).
        /// </summary>
        public IReadOnlyList<CheckRecord> GetMoveChecks(int moveId)
            => GetChecks("Move", moveId);

        /// <summary>
        /// Returns all Aplus/script checks for a node.
        /// These are routed to ICustomizationApi.EvaluateScriptCheckAsync.
        /// </summary>
        public IReadOnlyList<CheckRecord> GetAplusChecks(int nodeId)
            => GetNodeChecks(nodeId)
                .Where(c => c.CheckType == CheckType.Aplus)
                .ToList()
                .AsReadOnly();

        /// <summary>
        /// Returns true if any checks exist for the specified owner.
        /// Fast path — avoids list allocation when no checks present.
        /// </summary>
        public bool HasChecks(string ownersType, int ownersNumber)
            => _cache.ContainsKey((ownersType, ownersNumber));

        /// <summary>
        /// Total number of active checks in the cache.
        /// </summary>
        public int TotalCheckCount
            => _cache.Values.Sum(v => v.Count);

        /// <summary>
        /// Total number of unique owner keys in the cache.
        /// </summary>
        public int UniqueOwnerCount
            => _cache.Count;

        // ----------------------------------------------------------------
        // Targeted invalidation
        // ----------------------------------------------------------------

        /// <summary>
        /// Replaces the checks for a specific owner without
        /// rebuilding the entire cache.
        /// Called when an operator edits a check at runtime.
        /// </summary>
        public void UpdateOwner(
            string ownersType,
            int ownersNumber,
            IEnumerable<CheckRecord> newChecks)
        {
            var key = (ownersType, ownersNumber);
            var updated = new Dictionary<(string, int),
                IReadOnlyList<CheckRecord>>(_cache)
            {
                [key] = newChecks
                    .Where(r => r.Enabled)
                    .OrderBy(r => r.CheckOrder)
                    .ToList()
                    .AsReadOnly()
            };
            _cache = updated;

            _logger.LogInformation(
                "Check table updated for owner " +
                "{OwnersType}/{OwnersNumber}",
                ownersType, ownersNumber);
        }
    }

    // ----------------------------------------------------------------
    // Check table domain types
    // ----------------------------------------------------------------

    /// <summary>
    /// A single check record from the check table.
    /// Maps to the NYT_Checks database table structure.
    /// </summary>
    public sealed class CheckRecord
    {
        /// <summary>Database primary key.</summary>
        public int CheckId { get; init; }

        /// <summary>
        /// The type of entity that owns this check.
        /// Examples: "Node", "Move", "NodeWithItin",
        ///           "Distance", "Aplus", "Itinerary"
        /// </summary>
        public string OwnersType { get; init; } = string.Empty;

        /// <summary>
        /// The ID of the entity that owns this check.
        /// For Node checks: the logical NodeId.
        /// For Move checks: concatenated from/to NodeIds.
        /// </summary>
        public int OwnersNumber { get; init; }

        /// <summary>
        /// The specific check type within the owner category.
        /// </summary>
        public CheckType CheckType { get; init; }

        /// <summary>
        /// Evaluation order when multiple checks exist for the same owner.
        /// Lower values evaluated first.
        /// </summary>
        public int CheckOrder { get; init; }

        /// <summary>
        /// True if this check is active and should be evaluated.
        /// Disabled checks (Enabled=0) are filtered out at load time.
        /// </summary>
        public bool Enabled { get; init; }

        /// <summary>
        /// For Aplus checks: the APL macro name to invoke via
        /// ICustomizationApi.EvaluateScriptCheckAsync.
        /// Examples: "APLCenterToLoadCheck", "APLHomeToCenterCheck"
        /// </summary>
        public string? AplusMacroName { get; init; }

        /// <summary>
        /// The node or move this check guards access to.
        /// This is what gets locked if all checks pass.
        /// </summary>
        public int GuardedResourceId { get; init; }

        /// <summary>
        /// Optional: engineer-specified description of what
        /// this check is protecting against.
        /// </summary>
        public string? Description { get; init; }

        /// <summary>
        /// Name of the engineer who last modified this check.
        /// From NYT check table OwnersName field.
        /// </summary>
        public string? EngineerName { get; init; }
    }

    /// <summary>
    /// Check types matching the NYT check table structure.
    /// </summary>
    public enum CheckType
    {
        /// <summary>Direct node resource check.</summary>
        Node = 1,

        /// <summary>Move (edge) resource check.</summary>
        Move = 2,

        /// <summary>Node check with itinerary context.</summary>
        NodeWithItinerary = 3,

        /// <summary>Distance-based speed/clearance check.</summary>
        Distance = 4,

        /// <summary>
        /// APL/JScript macro check — routed to
        /// ICustomizationApi.EvaluateScriptCheckAsync.
        /// </summary>
        Aplus = 5,

        /// <summary>Itinerary-conditional check.</summary>
        Itinerary = 6
    }
}