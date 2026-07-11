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

    // ── Zoom/Pan state ──────────────────────────────────────────────
    let vpX = 0, vpY = 0;
    let vpW = 1400, vpH = 420;
    const VP_W0 = 1400, VP_H0 = 420;
    const MIN_ZOOM = 1.0;
    const MAX_ZOOM = 5.0;
    let isDragging = false;
    let dragStartX = 0, dragStartY = 0;
    let vpStartX = 0, vpStartY = 0;

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

    function applyViewBox() {
        const svg = document.getElementById('fleet-map');
        if (svg) svg.setAttribute('viewBox', `${vpX} ${vpY} ${vpW} ${vpH}`);
    }

    function clampPan() {
        vpX = Math.max(0, Math.min(vpX, VP_W0 - vpW));
        vpY = Math.max(0, Math.min(vpY, VP_H0 - vpH));
    }

    function zoom(factor, centerX, centerY) {
        const newW = Math.min(VP_W0, Math.max(VP_W0 / MAX_ZOOM, vpW * factor));
        const newH = Math.min(VP_H0, Math.max(VP_H0 / MAX_ZOOM, vpH * factor));
        vpX += (vpW - newW) * ((centerX - vpX) / vpW);
        vpY += (vpH - newH) * ((centerY - vpY) / vpH);
        vpW = newW;
        vpH = newH;
        clampPan();
        applyViewBox();
    }
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

        // ── Zoom/Pan event listeners ─────────────────────────────────
        svg.addEventListener('wheel', function (e) {
            e.preventDefault();
            const rect = svg.getBoundingClientRect();
            const mx = vpX + (e.clientX - rect.left) / rect.width * vpW;
            const my = vpY + (e.clientY - rect.top) / rect.height * vpH;
            const factor = e.deltaY > 0 ? 1.15 : 0.87;
            zoom(factor, mx, my);
        }, { passive: false });

        svg.addEventListener('mousedown', function (e) {
            if (e.button !== 0) return;
            isDragging = true;
            dragStartX = e.clientX;
            dragStartY = e.clientY;
            vpStartX = vpX;
            vpStartY = vpY;
            svg.style.cursor = 'grabbing';
        });

        document.addEventListener('mousemove', function (e) {
            if (!isDragging) return;
            const rect = document.getElementById('fleet-map').getBoundingClientRect();
            const dx = (e.clientX - dragStartX) / rect.width * vpW;
            const dy = (e.clientY - dragStartY) / rect.height * vpH;
            vpX = vpStartX - dx;
            vpY = vpStartY - dy;
            clampPan();
            applyViewBox();
        });

        document.addEventListener('mouseup', function () {
            if (!isDragging) return;
            isDragging = false;
            const svg = document.getElementById('fleet-map');
            if (svg) svg.style.cursor = 'grab';
        });

        svg.style.cursor = 'grab';

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

            let targetX, targetY;

            if (nodeId && nodePositions[nodeId]) {
                targetX = nodePositions[nodeId].x;
                targetY = nodePositions[nodeId].y;
            } else if (x != null && y != null) {
                targetX = toSvgX(parseFloat(x));
                targetY = toSvgY(parseFloat(y));
            } else {
                return;
            }

            // First placement — no animation
            if (!v.positioned) {
                v.dot.setAttribute('cx', targetX);
                v.dot.setAttribute('cy', targetY);
                v.label.setAttribute('x', targetX);
                v.label.setAttribute('y', targetY);
                v.currentX = targetX;
                v.currentY = targetY;
                v.positioned = true;
                return;
            }

            // Cancel any existing animation
            if (v.animFrame) cancelAnimationFrame(v.animFrame);

            const startX = v.currentX;
            const startY = v.currentY;
            const duration = 250; // 0.25 seconds in ms
            const startTime = performance.now();

            function animate(now) {
                const elapsed = now - startTime;
                const t = Math.min(elapsed / duration, 1);

                const cx = startX + (targetX - startX) * t;
                const cy = startY + (targetY - startY) * t;

                v.dot.setAttribute('cx', cx);
                v.dot.setAttribute('cy', cy);
                v.label.setAttribute('x', cx);
                v.label.setAttribute('y', cy);

                v.currentX = cx;
                v.currentY = cy;

                if (t < 1) {
                    v.animFrame = requestAnimationFrame(animate);
                }
            }

            v.animFrame = requestAnimationFrame(animate);
        },

        /** Update vehicle state (changes dot color). */
        updateState: function (vehicleId, activityState) {
            const labelColor = stateLabelColor(activityState);
            v.label.setAttribute('fill', labelColor);

            const v = vehicles[vehicleId];
            if (!v) return;

            const stateClass = activityStateToClass(activityState);
            if (v.state !== stateClass) {
                v.dot.setAttribute('class', `vehicle-dot ${stateClass}`);
                v.state = stateClass;
            }
        },

        setDotNetRef: function (dotNetRef) {
            window.fleetMap._dotNetRef = dotNetRef;
        },

        zoomIn: function () {
            zoom(0.7, vpX + vpW / 2, vpY + vpH / 2);
        },
        zoomOut: function () {
            zoom(1.4, vpX + vpW / 2, vpY + vpH / 2);
        },
        resetView: function () {
            vpX = 0; vpY = 0;
            vpW = VP_W0; vpH = VP_H0;
            applyViewBox();
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

    function stateLabelColor(state) {
        switch (state) {
            case 'Idle': return '#94a3b8';
            default: return '#ffffff';
        }
    }

    document.addEventListener('click', function (e) {
        const target = e.target;

        if (target.classList.contains('vehicle-dot')) {
            const vid = parseInt(target.getAttribute('data-vid'));
            console.log('Vehicle dot clicked:', vid);
            setTimeout(function () {
                if (window.fleetMap._dotNetRef)
                    window.fleetMap._dotNetRef.invokeMethodAsync('OnVehicleClicked', vid);
            }, 0);
            return;
        }

        if (target.classList.contains('popup-close')) {
            setTimeout(function () {
                if (window.fleetMap._dotNetRef)
                    window.fleetMap._dotNetRef.invokeMethodAsync('ClosePopup');
            }, 0);
            return;
        }
    });
})();
