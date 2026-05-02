using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Entities;
using AGV.Core.Enums;
using AGV.Core.ValueObjects;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AGV.Topology.Services
{
    /// <summary>
    /// Loads the versioned road network topology from SQL Server
    /// and builds the in-memory RoadMapGraph.
    ///
    /// TopologyService is the bridge between the persistent topology
    /// schema (nodes, moves, areas, versions) and the in-memory graph
    /// structures used by routing and traffic management.
    ///
    /// Uses Dapper for all database access — the topology load is a
    /// read-heavy, infrequent operation (at startup and on version
    /// change) so the lightweight Dapper approach is appropriate here.
    ///
    /// The service:
    ///   1. Polls for newer topology versions at configured interval
    ///   2. Loads all effective nodes, moves, and areas for the version
    ///   3. Constructs a new RoadMapGraph
    ///   4. Activates the new version via TopologyVersionManager
    ///   5. Loads engineer-specified NodeBlocks and MoveBlocks
    ///      into RuntimeBlockingState
    /// </summary>
    public sealed class TopologyService
    {
        private readonly string _connectionString;
        private readonly TopologyVersionManager _versionManager;
        private readonly RuntimeBlockingState _blockingState;

        public TopologyService(
            string connectionString,
            TopologyVersionManager versionManager,
            RuntimeBlockingState blockingState)
        {
            _connectionString = connectionString
                ?? throw new ArgumentNullException(nameof(connectionString));
            _versionManager = versionManager
                ?? throw new ArgumentNullException(nameof(versionManager));
            _blockingState = blockingState
                ?? throw new ArgumentNullException(nameof(blockingState));
        }

        // ----------------------------------------------------------------
        // Version polling
        // ----------------------------------------------------------------

        /// <summary>
        /// Checks whether a newer topology version is available in the
        /// database. If so, loads and activates it.
        /// Returns true if a new version was loaded.
        /// </summary>
        public async Task<bool> CheckAndLoadLatestVersionAsync(
            string loadedByUser = "system",
            CancellationToken cancellationToken = default)
        {
            var latest = await GetLatestVersionInfoAsync(cancellationToken);
            if (latest is null) return false;

            if (!_versionManager.IsNewerVersionAvailable(latest.VersionId))
                return false;

            await LoadVersionAsync(
                latest.VersionId,
                latest.VersionLabel,
                loadedByUser,
                cancellationToken);

            return true;
        }

        /// <summary>
        /// Forces a load of the specified topology version regardless
        /// of whether it is newer than the active version.
        /// Used for explicit rollback or forced refresh.
        /// </summary>
        public async Task LoadVersionAsync(
            int versionId,
            string versionLabel,
            string loadedByUser = "system",
            CancellationToken cancellationToken = default)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            // Load all topology data in parallel
            var nodesTask = LoadNodesAsync(connection, versionId);
            var movesTask = LoadMovesAsync(connection, versionId);
            var areasTask = LoadAreasAsync(connection, versionId);
            var membershipTask = LoadAreaMembershipsAsync(connection, versionId);

            await Task.WhenAll(nodesTask, movesTask, areasTask, membershipTask);

            var nodes = await nodesTask;
            var moves = await movesTask;
            var areas = await areasTask;
            var memberships = await membershipTask;

            // Build the in-memory graph
            var graph = new RoadMapGraph(
                versionId,
                nodes,
                moves,
                areas,
                memberships);

            // Activate the new version
            _versionManager.ActivateVersion(
                versionId, versionLabel, loadedByUser, graph);

            // Load engineer-specified blocks into runtime state
            await LoadEngineerBlocksAsync(connection, versionId);
        }

        // ----------------------------------------------------------------
        // Node loading
        // ----------------------------------------------------------------

        private async Task<IEnumerable<Node>> LoadNodesAsync(
            SqlConnection connection,
            int versionId)
        {
            const string sql = @"
                SELECT
                    NodeRecordId,
                    NodeId,
                    EffectiveFromVersionId,
                    IsDeleted,
                    NodeName,
                    X,
                    Y,
                    Z,
                    NodeType,
                    MapId
                FROM Node
                WHERE EffectiveFromVersionId = (
                    SELECT MAX(n2.EffectiveFromVersionId)
                    FROM Node n2
                    WHERE n2.NodeId = Node.NodeId
                      AND n2.EffectiveFromVersionId <= @VersionId
                )";

            var rows = await connection.QueryAsync<NodeRow>(sql,
                new { VersionId = versionId });

            return rows.Select(r => new Node(
                nodeId: r.NodeId,
                effectiveFromVersionId: r.EffectiveFromVersionId,
                position: new Coordinate(r.X, r.Y, r.Z),
                nodeType: (NodeType)r.NodeType,
                mapId: r.MapId,
                nodeName: r.NodeName));
        }

        // ----------------------------------------------------------------
        // Move loading
        // ----------------------------------------------------------------

        private async Task<IEnumerable<Move>> LoadMovesAsync(
            SqlConnection connection,
            int versionId)
        {
            const string sql = @"
                SELECT
                    MoveRecordId,
                    MoveId,
                    EffectiveFromVersionId,
                    IsDeleted,
                    FromNodeId,
                    ToNodeId,
                    RoutingTypeId,
                    TravelDirection,
                    StartHeading,
                    EndHeading,
                    ParameterA,
                    ArcLength,
                    DefaultSpeed,
                    MaxSpeed,
                    MaxWeightCapacityKg
                FROM Move
                WHERE EffectiveFromVersionId = (
                    SELECT MAX(m2.EffectiveFromVersionId)
                    FROM Move m2
                    WHERE m2.MoveId = Move.MoveId
                      AND m2.EffectiveFromVersionId <= @VersionId
                )";

            var rows = await connection.QueryAsync<MoveRow>(sql,
                new { VersionId = versionId });

            return rows.Select(r => new Move(
                moveId: r.MoveId,
                effectiveFromVersionId: r.EffectiveFromVersionId,
                fromNodeId: r.FromNodeId,
                toNodeId: r.ToNodeId,
                routingTypeId: r.RoutingTypeId,
                travelDirection: (TravelDirection)r.TravelDirection,
                clothoid: new ClothoidParameters(
                    r.StartHeading,
                    r.EndHeading,
                    r.ParameterA,
                    r.ArcLength),
                speed: new SpeedConstraint(
                    r.DefaultSpeed,
                    r.MaxSpeed),
                maxWeightCapacityKg: r.MaxWeightCapacityKg));
        }

        // ----------------------------------------------------------------
        // Area loading
        // ----------------------------------------------------------------

        private async Task<IEnumerable<Area>> LoadAreasAsync(
            SqlConnection connection,
            int versionId)
        {
            const string sql = @"
                SELECT
                    AreaRecordId,
                    AreaId,
                    EffectiveFromVersionId,
                    IsDeleted,
                    AreaName,
                    Description,
                    MaxVehicleCount
                FROM Area
                WHERE EffectiveFromVersionId = (
                    SELECT MAX(a2.EffectiveFromVersionId)
                    FROM Area a2
                    WHERE a2.AreaId = Area.AreaId
                      AND a2.EffectiveFromVersionId <= @VersionId
                )";

            var rows = await connection.QueryAsync<AreaRow>(sql,
                new { VersionId = versionId });

            return rows.Select(r => new Area(
                areaId: r.AreaId,
                effectiveFromVersionId: r.EffectiveFromVersionId,
                areaName: r.AreaName,
                maxVehicleCount: r.MaxVehicleCount,
                description: r.Description));
        }

        // ----------------------------------------------------------------
        // Area membership loading
        // ----------------------------------------------------------------

        private async Task<IEnumerable<(int NodeId, int AreaId)>>
            LoadAreaMembershipsAsync(
                SqlConnection connection,
                int versionId)
        {
            const string sql = @"
                SELECT NodeId, AreaId
                FROM AreaNode
                WHERE IsDeleted = 0
                  AND EffectiveFromVersionId <= @VersionId";

            var rows = await connection.QueryAsync<AreaMembershipRow>(sql,
                new { VersionId = versionId });

            return rows.Select(r => (r.NodeId, r.AreaId));
        }

        // ----------------------------------------------------------------
        // Engineer block loading
        // ----------------------------------------------------------------

        private async Task LoadEngineerBlocksAsync(
            SqlConnection connection,
            int versionId)
        {
            // Load NodeBlocks
            const string nodeBlockSql = @"
                SELECT NodeId, BlockReason, Description
                FROM NodeBlock
                WHERE IsEngineerBlock = 1";

            var nodeBlocks = await connection.QueryAsync<EngineerBlockRow>(
                nodeBlockSql);

            foreach (var block in nodeBlocks)
            {
                _blockingState.BlockNode(block.NodeId,
                    new NodeBlockRecord
                    {
                        LockedByVehicleId = null,
                        Reason = (BlockReason)block.BlockReason,
                        Description = block.Description
                    });
            }

            // Load MoveBlocks
            const string moveBlockSql = @"
                SELECT MoveId, BlockReason, Description
                FROM MoveBlock
                WHERE IsEngineerBlock = 1";

            var moveBlocks = await connection.QueryAsync<EngineerBlockRow>(
                moveBlockSql);

            foreach (var block in moveBlocks)
            {
                _blockingState.BlockMove(block.MoveId,
                    new MoveBlockRecord
                    {
                        LockedByVehicleId = null,
                        Reason = (BlockReason)block.BlockReason,
                        Description = block.Description
                    });
            }
        }

        // ----------------------------------------------------------------
        // Version info query
        // ----------------------------------------------------------------

        private async Task<VersionInfoRow?> GetLatestVersionInfoAsync(
            CancellationToken cancellationToken)
        {
            const string sql = @"
                SELECT TOP 1
                    VersionId,
                    VersionLabel
                FROM RoadmapVersion
                WHERE IsActive = 1
                ORDER BY VersionId DESC";

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            return await connection.QueryFirstOrDefaultAsync<VersionInfoRow>(sql);
        }

        // ----------------------------------------------------------------
        // Dapper row types (private — database mapping only)
        // ----------------------------------------------------------------

        private sealed class NodeRow
        {
            public int NodeRecordId { get; init; }
            public int NodeId { get; init; }
            public int EffectiveFromVersionId { get; init; }
            public bool IsDeleted { get; init; }
            public string? NodeName { get; init; }
            public decimal X { get; init; }
            public decimal Y { get; init; }
            public decimal Z { get; init; }
            public byte NodeType { get; init; }
            public string MapId { get; init; } = string.Empty;
        }

        private sealed class MoveRow
        {
            public int MoveRecordId { get; init; }
            public int MoveId { get; init; }
            public int EffectiveFromVersionId { get; init; }
            public bool IsDeleted { get; init; }
            public int FromNodeId { get; init; }
            public int ToNodeId { get; init; }
            public int RoutingTypeId { get; init; }
            public byte TravelDirection { get; init; }
            public decimal StartHeading { get; init; }
            public decimal EndHeading { get; init; }
            public decimal ParameterA { get; init; }
            public decimal ArcLength { get; init; }
            public decimal DefaultSpeed { get; init; }
            public decimal MaxSpeed { get; init; }
            public decimal? MaxWeightCapacityKg { get; init; }
        }

        private sealed class AreaRow
        {
            public int AreaRecordId { get; init; }
            public int AreaId { get; init; }
            public int EffectiveFromVersionId { get; init; }
            public bool IsDeleted { get; init; }
            public string AreaName { get; init; } = string.Empty;
            public string? Description { get; init; }
            public int? MaxVehicleCount { get; init; }
        }

        private sealed class AreaMembershipRow
        {
            public int NodeId { get; init; }
            public int AreaId { get; init; }
        }

        private sealed class EngineerBlockRow
        {
            public int NodeId { get; init; }
            public int MoveId { get; init; }
            public byte BlockReason { get; init; }
            public string? Description { get; init; }
        }

        private sealed class VersionInfoRow
        {
            public int VersionId { get; init; }
            public string VersionLabel { get; init; } = string.Empty;
        }
    }
}
