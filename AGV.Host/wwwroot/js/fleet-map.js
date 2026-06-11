/**
 * fleet-map.js
 * Renders the NYT College Point road map as SVG and animates
 * vehicle positions received from the SignalR hub.
 *
 * Called from _Host.cshtml after Blazor loads.
 * The SignalR connection is managed by Dashboard.razor;
 * vehicle positions are forwarded here via window.fleetMap.updateVehicle().
 */

(function () {
    'use strict';

    // ----------------------------------------------------------------
    // Coordinate mapping
    // Facility: X 466–680 ft, Y 478–542 ft  (Y increases north in DXF)
    // SVG canvas: 1400 × 420 px with 30px padding
    // ----------------------------------------------------------------
    const FACILITY = {
        xMin: 14997, xMax: 20555,
        yMin: 14950, yMax: 16190
    };

    const SVG_W = 1400, SVG_H = 420, PAD = 30;
    const DRAW_W = SVG_W - PAD * 2;
    const DRAW_H = SVG_H - PAD * 2;

    function toSvgX(fx) {
        return PAD + ((fx - FACILITY.xMin) / (FACILITY.xMax - FACILITY.xMin)) * DRAW_W;
    }

    function toSvgY(fy) {
        // Flip Y: DXF Y increases north, SVG Y increases down
        return PAD + ((FACILITY.yMax - fy) / (FACILITY.yMax - FACILITY.yMin)) * DRAW_H;
    }

    // ----------------------------------------------------------------
    // Node type classification
    // ----------------------------------------------------------------
    function nodeClass(node) {
        const name = node.nodeName || '';
        if (name.startsWith('LPS') || name.startsWith('UPS')) return 'map-node-press';
        if (name.startsWith('LC') || name.startsWith('UC') ||
            name.startsWith('MC')) return 'map-node-charge';
        if (name.startsWith('STG')) return 'map-node-staging';
        return 'map-node';
    }

    // ----------------------------------------------------------------
    // Vehicle state tracking
    // ----------------------------------------------------------------
    const vehicles = {};       // vehicleId → { dot, label, state }
    const nodePositions = {};  // nodeId → { x, y }

    // ----------------------------------------------------------------
    // Load roadmap and render
    // ----------------------------------------------------------------
    async function init() {
        if (init._done) return;
        init._done = true;

        let roadmap;
        try {
            const resp = await fetch('/nyt_agv_roadmap.json');
            roadmap = await resp.json();
        } catch (e) {
            console.warn('fleet-map: could not load roadmap JSON', e);
            return;
        }

        const svg = document.getElementById('fleet-map');
        if (!svg) return;

        const edgesLayer   = document.getElementById('edges-layer');
        const nodesLayer   = document.getElementById('nodes-layer');
        const vehicleLayer = document.getElementById('vehicles-layer');

        // ── Build node position lookup ──────────────────────────────
        for (const node of roadmap.nodes) {
            const sx = toSvgX(node.x);
            const sy = toSvgY(node.y);
            nodePositions[node.nodeId] = { x: sx, y: sy };
        }

        const crossIds = (window.dashboardConfig && window.dashboardConfig.crossCorridorMoveIds)
            ? new Set(window.dashboardConfig.crossCorridorMoveIds)
            : new Set();

        // ── Render edges ────────────────────────────────────────────
        for (const edge of roadmap.edges) {
            const from = nodePositions[edge.startNodeId];
            const to   = nodePositions[edge.endNodeId];
            if (!from || !to) continue;

            const line = document.createElementNS(
                'http://www.w3.org/2000/svg', 'line');
            line.setAttribute('x1', from.x);
            line.setAttribute('y1', from.y);
            line.setAttribute('x2', to.x);
            line.setAttribute('y2', to.y);

            // Cross-corridor connections get a brighter style
            const isCross = crossIds.has(edge.edgeId);
            line.setAttribute('class', isCross ? 'map-edge-cross' : 'map-edge');
            edgesLayer.appendChild(line);
        }

        // ── Render nodes ────────────────────────────────────────────
        for (const node of roadmap.nodes) {
            const pos = nodePositions[node.nodeId];
            if (!pos) continue;

            const circle = document.createElementNS(
                'http://www.w3.org/2000/svg', 'circle');
            circle.setAttribute('cx', pos.x);
            circle.setAttribute('cy', pos.y);
            circle.setAttribute('class', nodeClass(node));
            nodesLayer.appendChild(circle);
        }

        // ── Initialise 20 vehicle dots (F01–F20) ─────────────────────
        // They start hidden at origin until first position update arrives
        for (let i = 1; i <= 20; i++) {
            const dot = document.createElementNS(
                'http://www.w3.org/2000/svg', 'circle');
            dot.setAttribute('cx', -100);
            dot.setAttribute('cy', -100);
            dot.setAttribute('class', 'vehicle-dot state-idle');
            dot.setAttribute('data-vid', i);

            const label = document.createElementNS(
                'http://www.w3.org/2000/svg', 'text');
            label.setAttribute('x', -100);
            label.setAttribute('y', -100);
            label.setAttribute('class', 'vehicle-label');
            label.textContent = `F${String(i).padStart(2, '0')}`;

            vehicleLayer.appendChild(dot);
            vehicleLayer.appendChild(label);
            vehicles[i] = { dot, label, state: 'state-idle' };
        }

        console.log('fleet-map: map rendered —',
            roadmap.nodes.length, 'nodes,',
            roadmap.edges.length, 'edges');
    }

    // ----------------------------------------------------------------
    // Public API — called from Dashboard.razor / SignalR callbacks
    // ----------------------------------------------------------------

    window.fleetMap = {

        init: init,

        /** Update vehicle position on the SVG map. */
        updatePosition: function (vehicleId, x, y, nodeId) {
            const v = vehicles[vehicleId];
            if (!v) return;

            let sx, sy;

            if (nodeId && nodePositions[nodeId]) {
                sx = nodePositions[nodeId].x;
                sy = nodePositions[nodeId].y;
            } else if (x != null && y != null) {
                sx = toSvgX(parseFloat(x));
                sy = toSvgY(parseFloat(y));
            } else {
                return;
            }

            // Suppress CSS transition on first position update
            if (!v.positioned) {
                v.dot.style.transition = 'none';
                v.label.style.transition = 'none';
                v.positioned = true;
                // Re-enable transition after first placement
                requestAnimationFrame(() => {
                    v.dot.style.transition = '';
                    v.label.style.transition = '';
                });
            }

            v.dot.setAttribute('cx', sx);
            v.dot.setAttribute('cy', sy);
            v.label.setAttribute('x', sx);
            v.label.setAttribute('y', sy);
        },

        /** Update vehicle state (changes dot color). */
        updateState: function (vehicleId, activityState) {
            const v = vehicles[vehicleId];
            if (!v) return;

            const stateClass = activityStateToClass(activityState);
            if (v.state !== stateClass) {
                v.dot.setAttribute('class', `vehicle-dot ${stateClass}`);
                v.state = stateClass;
            }
        },
    };

    // ----------------------------------------------------------------
    // Helper
    // ----------------------------------------------------------------

    function activityStateToClass(state) {
        switch (state) {
            case 'TravelingToPickup':
            case 'ApproachingStand':
            case 'TravelingEmpty':
                return 'state-traveling';
            case 'TravelingLoaded':
            case 'ApproachingDrop':
                return 'state-loaded';
            case 'Picking':
            case 'Dropping':
                return 'state-forking';
            case 'OpportunityCharging':
            case 'MandatoryCharging':
            case 'QueuedForCharge':
                return 'state-charging';
            default:
                return 'state-idle';
        }
    }

    // ----------------------------------------------------------------
    // Boot
    // ----------------------------------------------------------------
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

})();
