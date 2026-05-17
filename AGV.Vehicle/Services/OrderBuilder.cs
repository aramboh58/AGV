using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Entities;
using AGV.Core.Interfaces;
using AGV.Topology.Services;

namespace AGV.Vehicle.Services
{
    /// <summary>
    /// Constructs VDA 5050 Order message payloads from host-side
    /// route results and vehicle fact sheets.
    ///
    /// The Order is the fundamental command unit in VDA 5050 —
    /// it contains an ordered sequence of nodes and edges that
    /// the vehicle executes autonomously. The host assembles the
    /// order and the vehicle's onboard navigation handles the
    /// actual path geometry (clothoid interpolation — Option B).
    ///
    /// Base/Horizon window:
    ///   The order is split into two portions:
    ///     Base     — released nodes/edges the vehicle executes immediately
    ///     Horizon  — unreleased look-ahead the vehicle is aware of
    ///
    ///   The window depth is bounded by VehicleFactSheet.MaxOrderHorizonDepth.
    ///   The host extends the base forward as the vehicle progresses,
    ///   triggered by lastNodeId updates in incoming State messages.
    ///
    ///   Target: extend base when buffer is ~50% consumed, to allow
    ///   for RF latency between order publication and vehicle receipt.
    ///
    /// Action attachment:
    ///   Actions (pick, drop, startCharging etc.) are attached to
    ///   the specific nodes where they should execute.
    ///   blockingType HARD = vehicle waits for action completion.
    ///   blockingType SOFT = vehicle continues while action executes.
    /// </summary>
    public sealed class OrderBuilder
    {
        private int _actionIdCounter;

        private readonly RoadMapGraphHolder _roadMapHolder;

        public OrderBuilder(RoadMapGraphHolder roadMapHolder)
        {
            _roadMapHolder = roadMapHolder
                ?? throw new ArgumentNullException(nameof(roadMapHolder));
        }
        private RoadMapGraph _roadMap => _roadMapHolder.GetRequired();
        // ----------------------------------------------------------------
        // Order construction
        // ----------------------------------------------------------------

        /// <summary>
        /// Builds a complete VDA 5050 Order for a mission dispatch.
        /// Sizes the base/horizon window based on the vehicle's
        /// declared MaxOrderHorizonDepth from its Fact Sheet.
        /// </summary>
        public VehicleOrder BuildMissionOrder(
            string orderId,
            int orderUpdateId,
            IReadOnlyList<RouteNode> routeNodes,
            IReadOnlyList<int> routeMoveIds,
            VehicleFactSheet factSheet,
            OrderActions actions)
        {
            var maxDepth = factSheet.MaxOrderHorizonDepth;
            var baseDepth = Math.Max(1,
                Math.Min(routeNodes.Count, maxDepth / 2));

            var nodes = BuildOrderNodes(
                routeNodes, actions, baseDepth);
            var edges = BuildOrderEdges(
                routeNodes, routeMoveIds, baseDepth);

            return new VehicleOrder
            {
                OrderId = orderId,
                OrderUpdateId = orderUpdateId,
                Nodes = nodes,
                Edges = edges,
            };
        }

        /// <summary>
        /// Builds a base extension order — extends the base forward
        /// as the vehicle progresses. Called when lastNodeId indicates
        /// the buffer is ~50% consumed.
        /// </summary>
        public VehicleOrder BuildBaseExtension(
            string orderId,
            int orderUpdateId,
            int lastNodeId,
            IReadOnlyList<RouteNode> remainingNodes,
            IReadOnlyList<int> remainingMoveIds,
            VehicleFactSheet factSheet,
            OrderActions actions)
        {
            var maxDepth = factSheet.MaxOrderHorizonDepth;
            var baseDepth = Math.Max(1,
                Math.Min(remainingNodes.Count, maxDepth / 2));

            var nodes = BuildOrderNodes(
                remainingNodes, actions, baseDepth);
            var edges = BuildOrderEdges(
                remainingNodes, remainingMoveIds, baseDepth);

            return new VehicleOrder
            {
                OrderId = orderId,
                OrderUpdateId = orderUpdateId,
                Nodes = nodes,
                Edges = edges,
            };
        }

        /// <summary>
        /// Builds a charge order — directs vehicle to a charge node
        /// and attaches startCharging action.
        /// </summary>
        public VehicleOrder BuildChargeOrder(
            string orderId,
            int orderUpdateId,
            IReadOnlyList<RouteNode> routeNodes,
            IReadOnlyList<int> routeMoveIds,
            VehicleFactSheet factSheet,
            string chargeActionType = "startCharging")
        {
            var chargeActions = new OrderActions
            {
                NodeActions = new Dictionary<int, List<OrderAction>>
                {
                    [routeNodes[^1].NodeId] = new List<OrderAction>
                    {
                        new OrderAction
                        {
                            ActionId     = NextActionId(),
                            ActionType   = chargeActionType,
                            BlockingType = "HARD",
                        }
                    }
                }
            };

            return BuildMissionOrder(
                orderId, orderUpdateId,
                routeNodes, routeMoveIds,
                factSheet, chargeActions);
        }

        // ----------------------------------------------------------------
        // Node and edge construction
        // ----------------------------------------------------------------

        private IReadOnlyList<OrderNode> BuildOrderNodes(
            IReadOnlyList<RouteNode> routeNodes,
            OrderActions actions,
            int baseDepth)
        {
            var result = new List<OrderNode>();

            for (int i = 0; i < routeNodes.Count; i++)
            {
                var routeNode = routeNodes[i];
                var mapNode = _roadMap.GetNode(routeNode.NodeId);
                var released = i < baseDepth;

                // Sequence IDs: nodes use even numbers (0,2,4...)
                // edges use odd numbers (1,3,5...) per VDA 5050 spec
                var sequenceId = i * 2;

                var nodeActions = new List<OrderAction>();
                if (actions.NodeActions.TryGetValue(
                    routeNode.NodeId, out var attachedActions))
                {
                    nodeActions.AddRange(attachedActions);
                }

                result.Add(new OrderNode
                {
                    NodeId = routeNode.NodeId.ToString(),
                    SequenceId = sequenceId,
                    Released = released,
                    X = mapNode?.Position.X ?? 0m,
                    Y = mapNode?.Position.Y ?? 0m,
                    MapId = mapNode?.MapId ?? "FLOOR_1",
                    Actions = nodeActions.AsReadOnly(),
                });
            }

            return result.AsReadOnly();
        }

        private IReadOnlyList<OrderEdge> BuildOrderEdges(
            IReadOnlyList<RouteNode> routeNodes,
            IReadOnlyList<int> routeMoveIds,
            int baseDepth)
        {
            var result = new List<OrderEdge>();

            for (int i = 0; i < routeMoveIds.Count; i++)
            {
                if (i >= routeNodes.Count - 1) break;

                var move = _roadMap.GetMoveById(routeMoveIds[i]);
                var released = i < baseDepth - 1;

                // Edge sequence IDs are odd numbers (1,3,5...)
                var sequenceId = i * 2 + 1;

                result.Add(new OrderEdge
                {
                    EdgeId = routeMoveIds[i].ToString(),
                    SequenceId = sequenceId,
                    Released = released,
                    StartNodeId = routeNodes[i].NodeId.ToString(),
                    EndNodeId = routeNodes[i + 1].NodeId.ToString(),
                    MaxSpeed = move?.Speed.MaxSpeed ?? 1.5m,
                    Actions = Array.Empty<OrderAction>(),
                });
            }

            return result.AsReadOnly();
        }

        private string NextActionId()
            => $"ACT{Interlocked.Increment(ref _actionIdCounter):D6}";

        // ----------------------------------------------------------------
        // Standard action builders
        // ----------------------------------------------------------------

        /// <summary>
        /// Builds a pick action for attachment to a pickup node.
        /// blockingType HARD — vehicle waits for fork operation.
        /// </summary>
        public OrderAction BuildPickAction()
            => new()
            {
                ActionId = NextActionId(),
                ActionType = "pick",
                BlockingType = "HARD",
                Parameters = Array.Empty<ActionParameter>(),
            };

        /// <summary>
        /// Builds a drop action for attachment to a dropoff node.
        /// </summary>
        public OrderAction BuildDropAction()
            => new()
            {
                ActionId = NextActionId(),
                ActionType = "drop",
                BlockingType = "HARD",
                Parameters = Array.Empty<ActionParameter>(),
            };

        /// <summary>
        /// Builds a waitForTrigger action — holds vehicle at a node
        /// pending host triggerRelease (traffic clearance).
        /// </summary>
        public OrderAction BuildWaitForTriggerAction(string triggerId)
            => new()
            {
                ActionId = NextActionId(),
                ActionType = "waitForTrigger",
                BlockingType = "HARD",
                Parameters = new List<ActionParameter>
                {
                    new() { Key = "triggerId", Value = triggerId }
                }.AsReadOnly(),
            };

        /// <summary>
        /// Builds a startCharging action.
        /// blockingType SOFT — vehicle can report state while charging.
        /// </summary>
        public OrderAction BuildStartChargingAction()
            => new()
            {
                ActionId = NextActionId(),
                ActionType = "startCharging",
                BlockingType = "SOFT",
                Parameters = Array.Empty<ActionParameter>(),
            };

        /// <summary>
        /// Builds a stopCharging action.
        /// </summary>
        public OrderAction BuildStopChargingAction()
            => new()
            {
                ActionId = NextActionId(),
                ActionType = "stopCharging",
                BlockingType = "SOFT",
                Parameters = Array.Empty<ActionParameter>(),
            };
    }

    /// <summary>
    /// Actions to attach to specific nodes in an order.
    /// Key: logical NodeId, Value: list of actions to attach.
    /// </summary>
    public sealed class OrderActions
    {
        public Dictionary<int, List<OrderAction>> NodeActions { get; init; }
            = new();

        public static OrderActions Empty => new();

        public static OrderActions WithPickAt(
            int nodeId, OrderAction pickAction)
            => new()
            {
                NodeActions = new Dictionary<int, List<OrderAction>>
                {
                    [nodeId] = new List<OrderAction> { pickAction }
                }
            };

        public static OrderActions WithDropAt(
            int nodeId, OrderAction dropAction)
            => new()
            {
                NodeActions = new Dictionary<int, List<OrderAction>>
                {
                    [nodeId] = new List<OrderAction> { dropAction }
                }
            };
    }
}
