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
    // Debug logging toggle
    // Set to true to re-enable the high-frequency per-move/per-route
    // console logging used during teleport/stall diagnosis. Left false
    // by default — this logging fires dozens of times per vehicle per
    // route, continuously, and is a real (client-side, main-thread)
    // cost that unthrottled server-side broadcasts previously masked.
    // ----------------------------------------------------------------
    const DEBUG_VERBOSE = false;

    // ----------------------------------------------------------------
    // Coordinate mapping
    // Facility: X 466–680 ft, Y 478–542 ft  (Y increases north in DXF)
    // SVG canvas: 1400 × 420 px with 30px padding
    // ----------------------------------------------------------------
    const FACILITY = {
        xMin: 47288, xMax: 67745,
        yMin: 48322, yMax: 54001
    };

    const SVG_W = 1400, SVG_H = 420, PAD = 30;
    const DRAW_W = SVG_W - PAD * 2;
    const DRAW_H = SVG_H - PAD * 2;

    function toSvgX(fx) {
        return PAD + ((fx - FACILITY.xMin) / (FACILITY.xMax - FACILITY.xMin)) * DRAW_W;
    }

    function toSvgY(fy) {
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
    const vehicles = {};
    const nodePositions = {};
    const edgeSpeeds = {}; // "fromId:toId" → speed in m/s
    let roadmap = null;

    // ----------------------------------------------------------------
    // Zoom/Pan state
    // ----------------------------------------------------------------
    let vpX = 0, vpY = 0;
    let vpW = 1400, vpH = 420;
    const VP_W0 = 1400, VP_H0 = 420;
    const MAX_ZOOM = 5.0;
    let isDragging = false;
    let dragStartX = 0, dragStartY = 0;
    let vpStartX = 0, vpStartY = 0;

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
    // Single shared animation loop
    // ----------------------------------------------------------------
    const animTargets = {};
    let animLoopRunning = false;

    function startAnimLoop() {
        if (animLoopRunning) return;
        animLoopRunning = true;
        requestAnimationFrame(animLoop);
    }

    function animLoop(now) {
        let anyActive = false;
        for (const [vid, target] of Object.entries(animTargets)) {
            const v = vehicles[parseInt(vid)];
            if (!v) continue;
            const duration = target.duration || 250;
            const elapsed = now - target.startTime;
            const t = Math.min(elapsed / duration, 1);
            const cx = target.startX + (target.targetX - target.startX) * t;
            const cy = target.startY + (target.targetY - target.startY) * t;
            const ch = target.startHeading + (target.targetHeading - target.startHeading) * t;
            v.group.setAttribute('transform',
                `translate(${cx},${cy}) rotate(${svgHeading(ch)})`);
            v.currentX = cx;
            v.currentY = cy;
            v.currentHeading = ch;
            v.label.setAttribute('transform', `rotate(${-svgHeading(ch)})`);

            if (t < 1) {
                anyActive = true;
            } else {
                if (target.onComplete) target.onComplete();
                if (animTargets[vid] === target) {
                    // onComplete didn't schedule a new move — safe to clear
                    delete animTargets[vid];
                } else if (animTargets[vid]) {
                    // onComplete scheduled a new move — keep the loop alive for it
                    anyActive = true;
                }
            }
        }
        if (anyActive) {
            requestAnimationFrame(animLoop);
        } else {
            animLoopRunning = false;
        }
    }

    // ----------------------------------------------------------------
    // Silhouette path functions
    // ----------------------------------------------------------------
    function forkSilhouette() {
        return [
            'M -8,-5 L 6,-5 L 6,5 L -8,5 Z',
            'M 6,-4 L 9,-4 L 9,4 L 6,4 Z',
            'M -2,-3 L 5,-3 L 5,3 L -2,3 Z',
            'M -9,-3 L -7,-3 L -7,3 L -9,3 Z',
            'M -18,-4 L -9,-4 L -9,-2 L -18,-2 Z',
            'M -18,2 L -9,2 L -9,4 L -18,4 Z',
        ].join(' ');
    }

    function wasteBinSilhouette() {
        return [
            'M -8,-5 L 8,-5 L 8,5 L -8,5 Z',
            'M -6,-4 L 4,-4 L 4,4 L -6,4 Z',
            'M 4,-3 L 7,-3 L 7,3 L 4,3 Z',
        ].join(' ');
    }

    // ----------------------------------------------------------------
    // Heading conversion
    // ----------------------------------------------------------------
    function svgHeading(facilityDegrees) {
        return -facilityDegrees;
    }

    // ----------------------------------------------------------------
    // Load roadmap and render
    // ----------------------------------------------------------------
    async function init() {
        if (init._done) return;
        init._done = true;

        try {
            const resp = await fetch('/nyt_agv_roadmap.json');
            roadmap = await resp.json();
        } catch (e) {
            console.warn('fleet-map: could not load roadmap JSON', e);
            return;
        }

        const svg = document.getElementById('fleet-map');
        if (!svg) return;

        const edgesLayer = document.getElementById('edges-layer');
        const nodesLayer = document.getElementById('nodes-layer');
        const vehicleLayer = document.getElementById('vehicles-layer');

        // ── Build node position lookup ──────────────────────────────
        for (const node of roadmap.nodes) {
            const sx = toSvgX(node.x);
            const sy = toSvgY(node.y);
            nodePositions[node.nodeId] = { x: sx, y: sy };
        }

        // ── Build edge speed lookup ──────────────────────────────────
        for (const edge of roadmap.edges) {
            edgeSpeeds[`${edge.startNodeId}:${edge.endNodeId}`] = {
                speed: edge.speed,
                distance: edge.distance
            };
        }

        const crossIds = (window.dashboardConfig && window.dashboardConfig.crossCorridorMoveIds)
            ? new Set(window.dashboardConfig.crossCorridorMoveIds)
            : new Set();

        // ── Render edges ────────────────────────────────────────────
        for (const edge of roadmap.edges) {
            const from = nodePositions[edge.startNodeId];
            const to = nodePositions[edge.endNodeId];
            if (!from || !to) continue;

            const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
            line.setAttribute('x1', from.x);
            line.setAttribute('y1', from.y);
            line.setAttribute('x2', to.x);
            line.setAttribute('y2', to.y);
            const isCross = crossIds.has(edge.edgeId);
            line.setAttribute('class', isCross ? 'map-edge-cross' : 'map-edge');
            edgesLayer.appendChild(line);
        }

        // ── Render nodes ────────────────────────────────────────────
        for (const node of roadmap.nodes) {
            const pos = nodePositions[node.nodeId];
            if (!pos) continue;

            const circle = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
            circle.setAttribute('cx', pos.x);
            circle.setAttribute('cy', pos.y);
            circle.setAttribute('class', nodeClass(node));
            nodesLayer.appendChild(circle);
        }

        // ── Initialise 20 vehicle groups (F01–F20) ──────────────────
        for (let i = 1; i <= 20; i++) {
            const g = document.createElementNS('http://www.w3.org/2000/svg', 'g');
            g.setAttribute('class', 'vehicle-group state-idle');
            g.setAttribute('data-vid', i);
            g.setAttribute('transform', 'translate(-100,-100) rotate(0)');

            const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
            path.setAttribute('class', 'vehicle-shape');
            path.setAttribute('d', forkSilhouette());
            path.setAttribute('pointer-events', 'all');

            const label = document.createElementNS('http://www.w3.org/2000/svg', 'text');
            label.setAttribute('class', 'vehicle-label');
            label.setAttribute('text-anchor', 'middle');
            label.setAttribute('dominant-baseline', 'middle');
            label.setAttribute('dy', '12');
            label.setAttribute('pointer-events', 'none');
            label.textContent = `F${String(i).padStart(2, '0')}`;

            const hitArea = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
            hitArea.setAttribute('x', '-25');
            hitArea.setAttribute('y', '-15');
            hitArea.setAttribute('width', '50');
            hitArea.setAttribute('height', '30');
            hitArea.setAttribute('fill', 'transparent');
            hitArea.setAttribute('pointer-events', 'all');
            g.appendChild(hitArea);

            g.appendChild(path);
            g.appendChild(label);
            vehicleLayer.appendChild(g);

            vehicles[i] = {
                group: g,
                path: path,
                label: label,
                state: 'state-idle',
                positioned: false,
                currentX: -100,
                currentY: -100,
                currentHeading: 0,
                vehicleType: 'Fork',
                routeNodeIds: [],
                routeIndex: 0,
                routeActive: false,
                routeVersion: 0,
            };
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
    // Internal per-vehicle update logic.
    // Both the single-item public functions (updatePosition/updateState)
    // and the batched public functions (updatePositions/updateStates)
    // call these — batching only changes how many times we cross the
    // JS interop boundary and how many DOM updates happen per call,
    // not the per-vehicle logic itself.
    // ----------------------------------------------------------------

    function applyPositionUpdate(vehicleId, x, y, nodeId, heading) {
        const v = vehicles[vehicleId];
        if (!v) return;

        // If dead-reckoning is active, use server position as sync correction
        if (v.routeActive && nodeId) {
            return;
        }

        // Non-dead-reckoning path (idle, charging, etc.)
        let targetX, targetY;
        let targetHeading = heading !== undefined ? heading : v.currentHeading;

        if (nodeId && nodePositions[parseInt(nodeId)]) {
            targetX = nodePositions[parseInt(nodeId)].x;
            targetY = nodePositions[parseInt(nodeId)].y;
        } else if (x != null && y != null) {
            targetX = toSvgX(parseFloat(x));
            targetY = toSvgY(parseFloat(y));
        } else {
            return;
        }

        if (!v.positioned) {
            v.group.setAttribute('transform',
                `translate(${targetX},${targetY}) rotate(${svgHeading(targetHeading)})`);
            v.currentX = targetX;
            v.currentY = targetY;
            v.currentHeading = targetHeading;
            v.positioned = true;
            return;
        }

        animTargets[vehicleId] = {
            targetX, targetY, targetHeading,
            startX: v.currentX,
            startY: v.currentY,
            startHeading: v.currentHeading,
            startTime: performance.now(),
            duration: 250
        };

        startAnimLoop();
    }

    function applyStateUpdate(vehicleId, activityState, vehicleType) {
        const v = vehicles[vehicleId];
        if (!v) return;

        const stateClass = activityStateToClass(activityState);
        if (v.state !== stateClass) {
            v.group.setAttribute('class', `vehicle-group ${stateClass}`);
            v.state = stateClass;
        }

        if (vehicleType && vehicleType !== v.vehicleType) {
            v.vehicleType = vehicleType;
            v.path.setAttribute('d',
                vehicleType === 'WasteBin'
                    ? wasteBinSilhouette()
                    : forkSilhouette());
        }

        const labelColor = stateLabelColor(activityState);
        v.label.setAttribute('fill', labelColor);
    }

    // ----------------------------------------------------------------
    // Public API
    // ----------------------------------------------------------------
    window.fleetMap = {

        init: init,

        // Single-item entry points — kept for compatibility / direct use.
        updatePosition: function (vehicleId, x, y, nodeId, heading) {
            applyPositionUpdate(vehicleId, x, y, nodeId, heading);
        },

        updateState: function (vehicleId, activityState, vehicleType) {
            applyStateUpdate(vehicleId, activityState, vehicleType);
        },

        // Batched entry points — one JS interop call per flush cycle,
        // looping over every vehicle in the batch internally instead of
        // Dashboard.razor calling into JS once per vehicle. Note: payload
        // property names arrive camelCased (Blazor's default JSON casing
        // for JS interop), e.g. item.vehicleId, item.nodeId.
        updatePositions: function (batch) {
            if (!batch) return;
            for (const item of batch) {
                applyPositionUpdate(
                    item.vehicleId, item.x, item.y, item.nodeId, item.heading);
            }
        },

        updateStates: function (batch) {
            if (!batch) return;
            for (const item of batch) {
                applyStateUpdate(
                    item.vehicleId, item.activityState, item.vehicleType);
            }
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

        setDotNetRef: function (dotNetRef) {
            window.fleetMap._dotNetRef = dotNetRef;
        },

        setVehicleRoute: function (vehicleId, routeNodeIds) {
            const v = vehicles[vehicleId];
            if (!v) return;
            if (!routeNodeIds || routeNodeIds.length === 0) {
                v.routeNodeIds = [];
                v.routeIndex = 0;
                v.routeActive = false;
                delete animTargets[vehicleId];
                v.routeVersion = (v.routeVersion || 0) + 1;
                if (DEBUG_VERBOSE)
                    console.log(`V${vehicleId} setVehicleRoute: 0 nodes, version now ${v.routeVersion}`);
                return;
            }
            delete animTargets[vehicleId];
            v.routeVersion = (v.routeVersion || 0) + 1;
            if (DEBUG_VERBOSE)
                console.log(`V${vehicleId} setVehicleRoute: ${routeNodeIds.length} nodes, version now ${v.routeVersion}`);
            const firstNodeId = routeNodeIds[0];
            const firstPos = nodePositions[firstNodeId];
            v.routeNodeIds = routeNodeIds;
            v.routeIndex = 0;
            v.routeActive = true;
            if (firstPos) {
                const dx = v.currentX - firstPos.x;
                const dy = v.currentY - firstPos.y;
                const dist = Math.sqrt(dx * dx + dy * dy);

                if (DEBUG_VERBOSE)
                    console.log(`V${vehicleId} route-start gap: dist=${dist.toFixed(1)}px from (${v.currentX.toFixed(0)},${v.currentY.toFixed(0)}) to firstNode=${firstNodeId}(${firstPos.x.toFixed(0)},${firstPos.y.toFixed(0)})`);

                if (dist > 2 && dist < 50) {
                    // Short gap — bridge smoothly
                    const distanceM = dist * ((FACILITY.xMax - FACILITY.xMin) / 100 / DRAW_W);
                    const bridgeDuration = Math.min(distanceM / 0.7 * 1000, 10000);
                    const routeVersion = v.routeVersion;
                    animTargets[vehicleId] = {
                        targetX: firstPos.x,
                        targetY: firstPos.y,
                        targetHeading: v.currentHeading,
                        startX: v.currentX,
                        startY: v.currentY,
                        startHeading: v.currentHeading,
                        startTime: performance.now(),
                        duration: bridgeDuration,
                        onComplete: function () {
                            if (v.routeVersion !== routeVersion) return;
                            v.currentX = firstPos.x;
                            v.currentY = firstPos.y;
                            scheduleNextMove(vehicleId);
                        }
                    };
                    startAnimLoop();
                    return;
                }
                // Large gap or already close — snap
                v.currentX = firstPos.x;
                v.currentY = firstPos.y;
                v.group.setAttribute('transform',
                    `translate(${firstPos.x},${firstPos.y}) rotate(${svgHeading(v.currentHeading)})`);
            }
            scheduleNextMove(vehicleId);
        },
    };

    // ----------------------------------------------------------------
    // Helpers
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

    function scheduleNextMove(vehicleId) {
        const v = vehicles[vehicleId];

        if (DEBUG_VERBOSE)
            console.log(`V${vehicleId} scheduleNextMove: index=${v.routeIndex}/${v.routeNodeIds.length} active=${v.routeActive} version=${v.routeVersion}`);

        if (!v || !v.routeActive) return;
        if (v.routeIndex >= v.routeNodeIds.length - 1) {
            v.routeActive = false;
            return;
        }

        const fromId = v.routeNodeIds[v.routeIndex];
        const toId = v.routeNodeIds[v.routeIndex + 1];
        const fromPos = nodePositions[fromId];
        const toPos = nodePositions[toId];

        if (!fromPos || !toPos) {
            if (DEBUG_VERBOSE)
                console.log(`V${vehicleId} missing node position: from=${fromId}(${!!fromPos}) to=${toId}(${!!toPos}) — skipping`);
            v.routeIndex++;
            scheduleNextMove(vehicleId);
            return;
        }

        const edgeKey = `${fromId}:${toId}`;
        const edgeData = edgeSpeeds[edgeKey];

        const distanceCm = edgeData ? edgeData.distance : 400;
        const distanceM = distanceCm / 100;
        const speedMs = edgeData ? edgeData.speed : 0.5;

        const heading = Math.atan2(
            -(toPos.y - fromPos.y),
            toPos.x - fromPos.x) * 180 / Math.PI;

        let durationMs;
        if (distanceM < 0.1) {
            const headingDelta = Math.abs(heading - v.currentHeading);
            const normalizedDelta = Math.min(headingDelta, 360 - headingDelta);
            durationMs = Math.max(200, (normalizedDelta / 90) * 3000);
        } else {
            durationMs = Math.max(200, (distanceM / speedMs) * 1000);
        }

        if (DEBUG_VERBOSE)
            console.log(`V${vehicleId} move ${fromId}→${toId}: ${distanceM.toFixed(3)}m @ ${speedMs}m/s = ${durationMs.toFixed(0)}ms`);

        const routeVersion = v.routeVersion;

        animTargets[vehicleId] = {
            targetX: toPos.x,
            targetY: toPos.y,
            targetHeading: heading,
            startX: v.currentX,
            startY: v.currentY,
            startHeading: v.currentHeading,
            startTime: performance.now(),
            duration: durationMs,
            onComplete: function () {
                if (v.routeVersion !== routeVersion) return;
                v.routeIndex++;
                scheduleNextMove(vehicleId);
            }
        };

        startAnimLoop();
    }

    // ----------------------------------------------------------------
    // Click handling
    // ----------------------------------------------------------------
    document.addEventListener('click', function (e) {
        const target = e.target;

        const group = target.closest('.vehicle-group');
        if (group) {
            const vid = parseInt(group.getAttribute('data-vid'));
            const clickTime = performance.now();
            console.log(`Vehicle clicked: ${vid} @ ${clickTime.toFixed(0)}ms`);
            setTimeout(function () {
                console.log(`  → setTimeout fired for V${vid} @ ${performance.now().toFixed(0)}ms (delay=${(performance.now() - clickTime).toFixed(0)}ms)`);
                if (window.fleetMap._dotNetRef) {
                    const invokeStart = performance.now();
                    window.fleetMap._dotNetRef.invokeMethodAsync('OnVehicleClicked', vid)
                        .then(() => console.log(`  → invokeMethodAsync resolved for V${vid} @ ${performance.now().toFixed(0)}ms (took ${(performance.now() - invokeStart).toFixed(0)}ms)`));
                }
            }, 0);
            return;
        }

        if (target.classList.contains('popup-close')) {
            setTimeout(async function () {
                if (window.fleetMap._dotNetRef)
                    await window.fleetMap._dotNetRef.invokeMethodAsync('ClosePopup');
            }, 0);
            return;
        }
    });

})();
