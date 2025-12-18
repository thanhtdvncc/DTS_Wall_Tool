/**
 * DtsCanvas.js - Standard Canvas Rendering Engine for DTS_Engine
 * Extracted from DTS_Engine/UI/Sample/index.html
 * 
 * This library provides:
 * - Generic canvas state management (zoom, pan, rotation, selection)
 * - 2D/3D projection and coordinate transformation
 * - Mouse/touch interaction handlers (click, drag, hover, wheel, pinch)
 * - Snap system for precision element placement
 * - Grid rendering and coordinate display
 * - Multi-view support (XY, XZ, YZ, 3D/ISO)
 * 
 * Usage:
 *   const canvas = new DtsCanvas('canvasId');
 *   canvas.setState({ blocks: [...], piles: [...] });
 *   canvas.setView('XY'); // or 'XZ', 'YZ', '3D'
 *   canvas.on('select', (type, id) => { ... });
 */

(function (global) {
    'use strict';

    // ==================== CORE STATE ====================
    class CanvasState {
        constructor() {
            // View state
            this.zoom = 1.0;
            this.panX = 0;
            this.panY = 0;
            this.rotateX = 0;  // For 3D views
            this.rotateY = 0;
            this.viewMode = 'XY';  // 'XY' | 'XZ' | 'YZ' | '3D' | 'ISO'

            // Selection state
            this.selection = { type: null, id: null };
            this.selectedIds = [];  // Multi-select support
            this.hoveredItem = null;

            // Snap settings
            this.snapStep = 0.1;    // Snap distance in meters
            this.gridStep = 0.05;   // Grid rounding step
            this.showGrid = true;
            this.showNames = true;

            // Display filters
            this.filters = {
                block: true,
                pile: true,
                point: true,
                machine: true,
                load: true
            };

            // Data
            this.blocks = [];
            this.piles = [];
            this.points = [];
            this.machines = [];
            this.loadPoints = [];

            // Listeners
            this._listeners = {};
        }

        // Event system
        on(event, callback) {
            if (!this._listeners[event]) this._listeners[event] = [];
            this._listeners[event].push(callback);
        }

        emit(event, ...args) {
            if (this._listeners[event]) {
                this._listeners[event].forEach(fn => fn(...args));
            }
        }

        // Data setters
        setData(data) {
            if (data.blocks) this.blocks = data.blocks;
            if (data.piles) this.piles = data.piles;
            if (data.points) this.points = data.points;
            if (data.machines) this.machines = data.machines;
            if (data.loadPoints) this.loadPoints = data.loadPoints;
            this.emit('dataChanged');
        }

        // Selection
        setSelection(type, id) {
            this.selection = { type, id };
            this.selectedIds = type && id ? [{ type, id }] : [];
            this.emit('selectionChanged', type, id);
        }

        isSelected(type, id) {
            return this.selectedIds.some(s => s.type === type && s.id === id);
        }

        // View controls
        setView(mode) {
            this.viewMode = mode;
            this.emit('viewChanged', mode);
        }

        resetView() {
            this.zoom = 1.0;
            this.panX = 0;
            this.panY = 0;
            this.rotateX = 0;
            this.rotateY = 0;
            this.emit('viewChanged', this.viewMode);
        }
    }

    // ==================== PROJECTION ====================
    class Projection {
        constructor(state, canvas) {
            this.state = state;
            this.canvas = canvas;
            this.scale = 50;  // pixels per meter
            this.originX = 0;
            this.originY = 0;
        }

        // Set scale and origin
        setOrigin(x, y) {
            this.originX = x;
            this.originY = y;
        }

        // 3D to 2D projection
        project(x, y, z) {
            const mode = this.state.viewMode;
            const s = this.scale * this.state.zoom;
            let px, py;

            if (mode === 'XY') {
                px = x * s;
                py = -y * s;  // Flip Y for screen coordinates
            } else if (mode === 'XZ') {
                px = x * s;
                py = -z * s;
            } else if (mode === 'YZ') {
                px = y * s;
                py = -z * s;
            } else {
                // 3D / ISO projection
                const isoAngle = Math.PI / 6; // 30 degrees
                const rotX = this.state.rotateX * Math.PI / 180;
                const rotY = this.state.rotateY * Math.PI / 180;

                // Apply rotation
                let rx = x * Math.cos(rotY) - z * Math.sin(rotY);
                let rz = x * Math.sin(rotY) + z * Math.cos(rotY);
                let ry = y * Math.cos(rotX) - rz * Math.sin(rotX);
                rz = y * Math.sin(rotX) + rz * Math.cos(rotX);

                // Isometric projection
                px = (rx - ry) * Math.cos(isoAngle) * s;
                py = -(rx + ry) * Math.sin(isoAngle) * s - rz * s;
            }

            return {
                x: this.originX + this.state.panX + px,
                y: this.originY + this.state.panY + py
            };
        }

        // Screen to world coordinates
        unproject(screenX, screenY) {
            const s = this.scale * this.state.zoom;
            const localX = screenX - this.originX - this.state.panX;
            const localY = screenY - this.originY - this.state.panY;

            const mode = this.state.viewMode;
            if (mode === 'XY') {
                return { x: localX / s, y: -localY / s, z: 0 };
            } else if (mode === 'XZ') {
                return { x: localX / s, y: 0, z: -localY / s };
            } else if (mode === 'YZ') {
                return { x: 0, y: localX / s, z: -localY / s };
            }
            return { x: localX / s, y: -localY / s, z: 0 };
        }
    }

    // ==================== PHYSICS / SNAP ====================
    class Physics {
        constructor(state) {
            this.state = state;
        }

        // Get interest points for snap (corners, midpoints, centers)
        getInterestPoints(block) {
            const pts = [];
            const b = block;

            // Center
            pts.push({ x: b.X + b.L / 2, y: b.Y + b.W / 2, z: b.Z + b.H / 2, type: 'center' });

            // 8 Corners
            const v = [
                { x: b.X, y: b.Y, z: b.Z },
                { x: b.X + b.L, y: b.Y, z: b.Z },
                { x: b.X + b.L, y: b.Y + b.W, z: b.Z },
                { x: b.X, y: b.Y + b.W, z: b.Z },
                { x: b.X, y: b.Y, z: b.Z + b.H },
                { x: b.X + b.L, y: b.Y, z: b.Z + b.H },
                { x: b.X + b.L, y: b.Y + b.W, z: b.Z + b.H },
                { x: b.X, y: b.Y + b.W, z: b.Z + b.H }
            ];
            v.forEach(p => pts.push({ ...p, type: 'corner' }));

            // Edge midpoints
            pts.push({ x: b.X + b.L / 2, y: b.Y, z: b.Z, type: 'mid' });
            pts.push({ x: b.X + b.L / 2, y: b.Y + b.W, z: b.Z, type: 'mid' });
            pts.push({ x: b.X, y: b.Y + b.W / 2, z: b.Z, type: 'mid' });
            pts.push({ x: b.X + b.L, y: b.Y + b.W / 2, z: b.Z, type: 'mid' });

            // Face centers
            pts.push({ x: b.X + b.L / 2, y: b.Y + b.W / 2, z: b.Z, type: 'face' });
            pts.push({ x: b.X + b.L / 2, y: b.Y + b.W / 2, z: b.Z + b.H, type: 'face' });

            return pts;
        }

        // Solve snap to nearest target
        solveSnap(movingObj, tentPos, step, viewMode) {
            if (step <= 0) {
                return { x: tentPos.x, y: tentPos.y, z: tentPos.z, snapped: false };
            }

            // Build targets from all elements
            const targets = [{ x: 0, y: 0, z: 0, type: 'origin' }];

            this.state.blocks.forEach(b => {
                if (b.id !== movingObj?.id) {
                    targets.push(...this.getInterestPoints(b));
                }
            });

            this.state.piles.forEach(p => {
                if (p.id !== movingObj?.id) {
                    targets.push({ x: p.X, y: p.Y, z: p.Z || 0, type: 'pile' });
                }
            });

            // Find nearest snap
            let bestShift = { x: 0, y: 0, z: 0 };
            let minDst = step * 2;
            let found = false;

            for (const t of targets) {
                const dx = t.x - tentPos.x;
                const dy = t.y - tentPos.y;
                const dz = t.z - tentPos.z;

                let dist;
                if (viewMode === 'XY') dist = Math.hypot(dx, dy);
                else if (viewMode === 'XZ') dist = Math.hypot(dx, dz);
                else if (viewMode === 'YZ') dist = Math.hypot(dy, dz);
                else dist = Math.hypot(dx, dy, dz);

                if (dist < minDst) {
                    minDst = dist;
                    bestShift = { x: dx, y: dy, z: dz };
                    found = true;
                }
            }

            if (found) {
                return {
                    x: tentPos.x + bestShift.x,
                    y: tentPos.y + bestShift.y,
                    z: tentPos.z + bestShift.z,
                    snapped: true
                };
            }

            return { x: tentPos.x, y: tentPos.y, z: tentPos.z, snapped: false };
        }

        // Grid rounding
        roundToGrid(value, step) {
            if (step <= 0) return value;
            return Math.round(value / step) * step;
        }
    }

    // ==================== RENDERER ====================
    class Renderer {
        constructor(canvas, state, projection, physics) {
            this.canvas = canvas;
            this.ctx = canvas.getContext('2d');
            this.state = state;
            this.projection = projection;
            this.physics = physics;

            // Colors
            this.colors = {
                block: { fill: '#60a5fa', stroke: '#3b82f6', selectedFill: '#fbbf24', selectedStroke: '#d97706' },
                pile: { fill: '#22c55e', stroke: '#15803d' },
                point: { fill: '#374151', stroke: '#374151' },
                machine: { fill: '#e879f9', stroke: '#a855f7' },
                grid: '#e2e8f0',
                axis: '#94a3b8'
            };
        }

        // Main draw
        draw() {
            const ctx = this.ctx;
            const canvas = this.canvas;

            // Clear
            ctx.fillStyle = '#f8fafc';
            ctx.fillRect(0, 0, canvas.width, canvas.height);

            // Set origin to center
            this.projection.setOrigin(canvas.width / 2, canvas.height / 2);

            // Draw grid
            if (this.state.showGrid) {
                this.drawGrid();
            }

            // Draw axes
            this.drawAxes();

            // Draw elements based on view
            this.drawBlocks();
            this.drawPiles();
            this.drawPoints();
        }

        drawGrid() {
            const ctx = this.ctx;
            const step = this.state.gridStep;
            if (step <= 0) return;

            const range = 10;  // meters
            ctx.strokeStyle = this.colors.grid;
            ctx.lineWidth = 0.5;
            ctx.setLineDash([2, 2]);

            for (let i = -range; i <= range; i += step) {
                if (i === 0) continue;

                const p1 = this.projection.project(i, -range, 0);
                const p2 = this.projection.project(i, range, 0);
                ctx.beginPath();
                ctx.moveTo(p1.x, p1.y);
                ctx.lineTo(p2.x, p2.y);
                ctx.stroke();

                const p3 = this.projection.project(-range, i, 0);
                const p4 = this.projection.project(range, i, 0);
                ctx.beginPath();
                ctx.moveTo(p3.x, p3.y);
                ctx.lineTo(p4.x, p4.y);
                ctx.stroke();
            }

            ctx.setLineDash([]);
        }

        drawAxes() {
            const ctx = this.ctx;
            const len = 5;  // meters

            ctx.lineWidth = 2;
            ctx.font = 'bold 12px sans-serif';

            const o = this.projection.project(0, 0, 0);

            // X axis (red)
            ctx.strokeStyle = '#ef4444';
            ctx.fillStyle = '#ef4444';
            const xEnd = this.projection.project(len, 0, 0);
            ctx.beginPath();
            ctx.moveTo(o.x, o.y);
            ctx.lineTo(xEnd.x, xEnd.y);
            ctx.stroke();
            ctx.fillText('X', xEnd.x + 5, xEnd.y);

            // Y axis (green)
            ctx.strokeStyle = '#22c55e';
            ctx.fillStyle = '#22c55e';
            const yEnd = this.projection.project(0, len, 0);
            ctx.beginPath();
            ctx.moveTo(o.x, o.y);
            ctx.lineTo(yEnd.x, yEnd.y);
            ctx.stroke();
            ctx.fillText('Y', yEnd.x + 5, yEnd.y);

            // Z axis (blue) - only in elevation views
            if (this.state.viewMode !== 'XY') {
                ctx.strokeStyle = '#3b82f6';
                ctx.fillStyle = '#3b82f6';
                const zEnd = this.projection.project(0, 0, len);
                ctx.beginPath();
                ctx.moveTo(o.x, o.y);
                ctx.lineTo(zEnd.x, zEnd.y);
                ctx.stroke();
                ctx.fillText('Z', zEnd.x + 5, zEnd.y);
            }
        }

        drawBlocks() {
            const ctx = this.ctx;

            this.state.blocks.forEach(b => {
                if (!this.state.filters.block) return;

                const sel = this.state.isSelected('block', b.id);
                const c = this.colors.block;

                ctx.fillStyle = sel ? c.selectedFill : c.fill;
                ctx.strokeStyle = sel ? c.selectedStroke : c.stroke;
                ctx.lineWidth = sel ? 2 : 1;
                ctx.globalAlpha = b.op === 0 ? 0.3 : 0.7;

                // Project 4 corners for XY view
                const p1 = this.projection.project(b.X, b.Y, b.Z);
                const p2 = this.projection.project(b.X + b.L, b.Y, b.Z);
                const p3 = this.projection.project(b.X + b.L, b.Y + b.W, b.Z);
                const p4 = this.projection.project(b.X, b.Y + b.W, b.Z);

                ctx.beginPath();
                ctx.moveTo(p1.x, p1.y);
                ctx.lineTo(p2.x, p2.y);
                ctx.lineTo(p3.x, p3.y);
                ctx.lineTo(p4.x, p4.y);
                ctx.closePath();
                ctx.fill();
                ctx.stroke();

                ctx.globalAlpha = 1.0;

                // Label
                if (this.state.showNames && b.name) {
                    const center = this.projection.project(b.X + b.L / 2, b.Y + b.W / 2, b.Z);
                    ctx.fillStyle = '#1e293b';
                    ctx.font = '10px sans-serif';
                    ctx.textAlign = 'center';
                    ctx.fillText(b.name, center.x, center.y);
                }
            });
        }

        drawPiles() {
            const ctx = this.ctx;

            this.state.piles.forEach(p => {
                if (!this.state.filters.pile) return;

                const sel = this.state.isSelected('pile', p.id);
                const c = this.colors.pile;
                const pt = this.projection.project(p.X, p.Y, p.Z || 0);
                const r = (p.size || 0.4) / 2 * this.projection.scale * this.state.zoom;

                ctx.fillStyle = sel ? this.colors.block.selectedFill : c.fill;
                ctx.strokeStyle = sel ? this.colors.block.selectedStroke : c.stroke;
                ctx.lineWidth = sel ? 2 : 1;

                ctx.beginPath();
                ctx.arc(pt.x, pt.y, Math.max(4, r), 0, Math.PI * 2);
                ctx.fill();
                ctx.stroke();
            });
        }

        drawPoints() {
            const ctx = this.ctx;

            this.state.points.forEach(p => {
                if (!this.state.filters.point) return;

                const sel = this.state.isSelected('point', p.id);
                const pt = this.projection.project(p.X, p.Y, p.Z || 0);
                const c = this.colors.point;

                ctx.strokeStyle = sel ? '#ef4444' : c.stroke;
                ctx.lineWidth = sel ? 2 : 1;

                // Crosshair
                const sz = 6;
                ctx.beginPath();
                ctx.moveTo(pt.x - sz, pt.y);
                ctx.lineTo(pt.x + sz, pt.y);
                ctx.moveTo(pt.x, pt.y - sz);
                ctx.lineTo(pt.x, pt.y + sz);
                ctx.stroke();

                // Dot
                ctx.fillStyle = sel ? '#ef4444' : c.fill;
                ctx.beginPath();
                ctx.arc(pt.x, pt.y, 3, 0, Math.PI * 2);
                ctx.fill();

                // Name
                if (this.state.showNames && p.name) {
                    ctx.fillStyle = sel ? '#ef4444' : '#374151';
                    ctx.font = '9px sans-serif';
                    ctx.fillText(p.name, pt.x + 8, pt.y - 4);
                }
            });
        }
    }

    // ==================== EVENT HANDLER ====================
    class EventHandler {
        constructor(canvas, state, projection, physics, renderer) {
            this.canvas = canvas;
            this.state = state;
            this.projection = projection;
            this.physics = physics;
            this.renderer = renderer;

            this.isDragging = false;
            this.isPanning = false;
            this.lastX = 0;
            this.lastY = 0;
            this.draggedItem = null;

            this.attach();
        }

        attach() {
            const canvas = this.canvas;

            // Mouse move
            canvas.addEventListener('mousemove', (e) => this.onMouseMove(e));

            // Mouse down
            canvas.addEventListener('mousedown', (e) => this.onMouseDown(e));

            // Mouse up
            canvas.addEventListener('mouseup', (e) => this.onMouseUp(e));

            // Mouse leave
            canvas.addEventListener('mouseleave', () => this.onMouseLeave());

            // Wheel zoom
            canvas.addEventListener('wheel', (e) => this.onWheel(e), { passive: false });

            // Double click reset
            canvas.addEventListener('dblclick', () => this.onDoubleClick());

            // Context menu
            canvas.addEventListener('contextmenu', (e) => e.preventDefault());
        }

        onMouseMove(e) {
            const rect = this.canvas.getBoundingClientRect();
            const x = e.clientX - rect.left;
            const y = e.clientY - rect.top;

            if (this.isPanning) {
                const dx = e.clientX - this.lastX;
                const dy = e.clientY - this.lastY;
                this.state.panX += dx;
                this.state.panY += dy;
                this.lastX = e.clientX;
                this.lastY = e.clientY;
                this.renderer.draw();
            } else if (this.isDragging && this.draggedItem) {
                // Drag element
                const worldPos = this.projection.unproject(x, y);
                const snapped = this.physics.solveSnap(
                    this.draggedItem.item,
                    worldPos,
                    this.state.snapStep,
                    this.state.viewMode
                );

                // Update position
                this.draggedItem.item.X = snapped.x;
                this.draggedItem.item.Y = snapped.y;
                this.draggedItem.item.Z = snapped.z;

                this.renderer.draw();
            } else {
                // Hover detection
                const worldPos = this.projection.unproject(x, y);
                this.state.emit('cursorMove', worldPos);
            }
        }

        onMouseDown(e) {
            this.lastX = e.clientX;
            this.lastY = e.clientY;

            if (e.button === 1 || (e.button === 0 && e.altKey)) {
                // Middle click or Alt+Left = pan
                this.isPanning = true;
                this.canvas.style.cursor = 'grabbing';
            } else if (e.button === 0) {
                // Left click = select or drag
                const rect = this.canvas.getBoundingClientRect();
                const x = e.clientX - rect.left;
                const y = e.clientY - rect.top;
                const worldPos = this.projection.unproject(x, y);

                // Hit test
                const hit = this.hitTest(worldPos);
                if (hit) {
                    this.state.setSelection(hit.type, hit.id);
                    this.isDragging = true;
                    this.draggedItem = hit;
                    this.canvas.style.cursor = 'move';
                } else {
                    this.state.setSelection(null, null);
                }
                this.renderer.draw();
            }
        }

        onMouseUp(e) {
            this.isPanning = false;
            this.isDragging = false;
            this.draggedItem = null;
            this.canvas.style.cursor = 'default';
        }

        onMouseLeave() {
            this.isPanning = false;
            this.isDragging = false;
            this.draggedItem = null;
            this.canvas.style.cursor = 'default';
        }

        onWheel(e) {
            e.preventDefault();
            const delta = e.deltaY > 0 ? 0.9 : 1.1;
            const newZoom = Math.max(0.2, Math.min(5, this.state.zoom * delta));

            // Zoom towards mouse
            const rect = this.canvas.getBoundingClientRect();
            const mouseX = e.clientX - rect.left;
            const mouseY = e.clientY - rect.top;

            this.state.panX = mouseX - (mouseX - this.state.panX) * (newZoom / this.state.zoom);
            this.state.panY = mouseY - (mouseY - this.state.panY) * (newZoom / this.state.zoom);
            this.state.zoom = newZoom;

            this.renderer.draw();
        }

        onDoubleClick() {
            this.state.resetView();
            this.renderer.draw();
        }

        hitTest(worldPos) {
            const tol = 0.5 / this.state.zoom;

            // Check blocks
            for (const b of this.state.blocks) {
                if (worldPos.x >= b.X && worldPos.x <= b.X + b.L &&
                    worldPos.y >= b.Y && worldPos.y <= b.Y + b.W) {
                    return { type: 'block', id: b.id, item: b };
                }
            }

            // Check piles
            for (const p of this.state.piles) {
                const dx = worldPos.x - p.X;
                const dy = worldPos.y - p.Y;
                if (Math.hypot(dx, dy) < (p.size || 0.4) / 2 + tol) {
                    return { type: 'pile', id: p.id, item: p };
                }
            }

            // Check points
            for (const pt of this.state.points) {
                const dx = worldPos.x - pt.X;
                const dy = worldPos.y - pt.Y;
                if (Math.hypot(dx, dy) < tol) {
                    return { type: 'point', id: pt.id, item: pt };
                }
            }

            return null;
        }
    }

    // ==================== MAIN CLASS ====================
    class DtsCanvas {
        constructor(canvasId) {
            this.canvas = document.getElementById(canvasId);
            if (!this.canvas) {
                console.error(`DtsCanvas: Canvas element '${canvasId}' not found`);
                return;
            }

            this.state = new CanvasState();
            this.projection = new Projection(this.state, this.canvas);
            this.physics = new Physics(this.state);
            this.renderer = new Renderer(this.canvas, this.state, this.projection, this.physics);
            this.events = new EventHandler(this.canvas, this.state, this.projection, this.physics, this.renderer);

            // Auto-draw on data/view changes
            this.state.on('dataChanged', () => this.renderer.draw());
            this.state.on('viewChanged', () => this.renderer.draw());
            this.state.on('selectionChanged', () => this.renderer.draw());
        }

        // Public API
        setData(data) {
            this.state.setData(data);
            return this;
        }

        setView(mode) {
            this.state.setView(mode);
            return this;
        }

        setZoom(zoom) {
            this.state.zoom = zoom;
            this.renderer.draw();
            return this;
        }

        setSnap(step) {
            this.state.snapStep = step;
            return this;
        }

        setGrid(step) {
            this.state.gridStep = step;
            this.renderer.draw();
            return this;
        }

        showGrid(show) {
            this.state.showGrid = show;
            this.renderer.draw();
            return this;
        }

        showNames(show) {
            this.state.showNames = show;
            this.renderer.draw();
            return this;
        }

        setFilter(type, visible) {
            this.state.filters[type] = visible;
            this.renderer.draw();
            return this;
        }

        select(type, id) {
            this.state.setSelection(type, id);
            return this;
        }

        getSelection() {
            return this.state.selection;
        }

        on(event, callback) {
            this.state.on(event, callback);
            return this;
        }

        redraw() {
            this.renderer.draw();
            return this;
        }

        resetView() {
            this.state.resetView();
            return this;
        }

        resize(width, height) {
            this.canvas.width = width;
            this.canvas.height = height;
            this.renderer.draw();
            return this;
        }

        // Export state
        getState() {
            return {
                blocks: this.state.blocks,
                piles: this.state.piles,
                points: this.state.points,
                machines: this.state.machines
            };
        }
    }

    // ==================== EXPORT ====================
    // Export for browser
    global.DtsCanvas = DtsCanvas;
    global.CanvasState = CanvasState;
    global.Projection = Projection;
    global.Physics = Physics;
    global.Renderer = Renderer;

    // Export for CommonJS
    if (typeof module !== 'undefined' && module.exports) {
        module.exports = { DtsCanvas, CanvasState, Projection, Physics, Renderer };
    }

})(typeof window !== 'undefined' ? window : global);
