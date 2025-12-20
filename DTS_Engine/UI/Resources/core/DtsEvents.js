/**
 * DtsEvents.js - Canvas Event Handlers
 * Manages pan, zoom, box-zoom, and mouse interactions.
 */
(function (global) {
    'use strict';

    const DtsEvents = {
        // ===== INTERNAL STATE =====
        _canvas: null,
        _isDragging: false,
        _isBoxZoom: false,
        _lastX: 0,
        _lastY: 0,
        _boxStartX: 0,
        _boxStartY: 0,
        _boxEndX: 0,
        _boxEndY: 0,
        _onRender: null,         // Callback to render canvas
        _onBoxZoomDraw: null,    // Callback to draw box zoom overlay

        /**
         * Initialize event handlers on canvas
         * @param {HTMLCanvasElement} canvas
         * @param {Function} renderCallback - Called when canvas needs redraw
         */
        init(canvas, renderCallback) {
            this._canvas = canvas;
            this._onRender = renderCallback;

            // Attach event listeners
            canvas.addEventListener('mousedown', this._onMouseDown.bind(this));
            canvas.addEventListener('mousemove', this._onMouseMove.bind(this));
            canvas.addEventListener('mouseup', this._onMouseUp.bind(this));
            canvas.addEventListener('mouseleave', this._onMouseLeave.bind(this));
            canvas.addEventListener('wheel', this._onWheel.bind(this), { passive: false });
            canvas.addEventListener('dblclick', this._onDblClick.bind(this));
            canvas.addEventListener('click', this._onClick.bind(this));
        },

        /**
         * Set callback for drawing box zoom overlay
         */
        setBoxZoomDrawCallback(callback) {
            this._onBoxZoomDraw = callback;
        },

        // ===== EVENT HANDLERS =====

        _onMouseDown(e) {
            const rect = this._canvas.getBoundingClientRect();
            const state = global.Dts?.State;

            // Ctrl+Left = Box Zoom
            if (e.button === 0 && e.ctrlKey) {
                this._isBoxZoom = true;
                this._boxStartX = e.clientX - rect.left;
                this._boxStartY = e.clientY - rect.top;
                this._boxEndX = this._boxStartX;
                this._boxEndY = this._boxStartY;
                this._canvas.style.cursor = 'crosshair';
                e.preventDefault();
                return;
            }

            // Middle click or Alt+Left = Pan
            if (e.button === 1 || (e.button === 0 && e.altKey)) {
                this._isDragging = true;
                this._lastX = e.clientX;
                this._lastY = e.clientY;
                this._canvas.style.cursor = 'grabbing';
                e.preventDefault();
            }
        },

        _onMouseMove(e) {
            const rect = this._canvas.getBoundingClientRect();
            const state = global.Dts?.State;

            // Box zoom drag
            if (this._isBoxZoom) {
                this._boxEndX = e.clientX - rect.left;
                this._boxEndY = e.clientY - rect.top;
                if (this._onRender) this._onRender();
                return;
            }

            // Pan drag
            if (this._isDragging && state) {
                const dx = e.clientX - this._lastX;
                const dy = e.clientY - this._lastY;
                state.panX += dx;
                state.panY += dy;
                this._lastX = e.clientX;
                this._lastY = e.clientY;
                if (this._onRender) this._onRender();
                return;
            }

            // Hover detection - emit event for viewer to handle
            const physics = global.Dts?.Physics;
            if (physics && state) {
                const pos = physics.screenToCanvas(e.clientX, e.clientY, rect);
                state.emit?.('mousemove', pos.x, pos.y, e);
            }
        },

        _onMouseUp(e) {
            const state = global.Dts?.State;

            // Finish box zoom
            if (this._isBoxZoom) {
                this._isBoxZoom = false;
                this._canvas.style.cursor = 'default';

                const x1 = Math.min(this._boxStartX, this._boxEndX);
                const y1 = Math.min(this._boxStartY, this._boxEndY);
                const x2 = Math.max(this._boxStartX, this._boxEndX);
                const y2 = Math.max(this._boxStartY, this._boxEndY);
                const boxW = x2 - x1;
                const boxH = y2 - y1;

                // Only zoom if box is reasonable size
                if (boxW > 20 && boxH > 20 && state) {
                    // Convert screen to world
                    const worldX1 = (x1 - state.panX) / state.zoom;
                    const worldY1 = (y1 - state.panY) / state.zoom;
                    const worldX2 = (x2 - state.panX) / state.zoom;
                    const worldY2 = (y2 - state.panY) / state.zoom;
                    const worldW = worldX2 - worldX1;
                    const worldH = worldY2 - worldY1;

                    // Calculate new zoom
                    const canvasW = this._canvas.width;
                    const canvasH = this._canvas.height;
                    const zoomX = canvasW / worldW;
                    const zoomY = canvasH / worldH;
                    const newZoom = Math.min(zoomX, zoomY, 5) * 0.9;

                    // Center the box
                    const worldCenterX = (worldX1 + worldX2) / 2;
                    const worldCenterY = (worldY1 + worldY2) / 2;

                    state.zoom = Math.max(0.5, newZoom);
                    state.panX = canvasW / 2 - worldCenterX * state.zoom;
                    state.panY = canvasH / 2 - worldCenterY * state.zoom;

                    if (this._onRender) this._onRender();
                }
                return;
            }

            this._isDragging = false;
            this._canvas.style.cursor = 'default';
        },

        _onMouseLeave(e) {
            this._isDragging = false;
            this._isBoxZoom = false;
            this._canvas.style.cursor = 'default';
        },

        _onWheel(e) {
            e.preventDefault();
            const state = global.Dts?.State;
            if (!state) return;

            const delta = e.deltaY > 0 ? 0.9 : 1.1;
            const newZoom = Math.max(0.5, Math.min(5, state.zoom * delta));
            const rect = this._canvas.getBoundingClientRect();
            const mouseX = e.clientX - rect.left;
            const mouseY = e.clientY - rect.top;

            // Zoom towards mouse position
            state.panX = mouseX - (mouseX - state.panX) * (newZoom / state.zoom);
            state.panY = mouseY - (mouseY - state.panY) * (newZoom / state.zoom);
            state.zoom = newZoom;

            if (this._onRender) this._onRender();
        },

        _onDblClick(e) {
            const state = global.Dts?.State;
            if (state) {
                state.resetView();
                if (this._onRender) this._onRender();
            }
        },

        _onClick(e) {
            // Let viewer handle click for selection
            const rect = this._canvas.getBoundingClientRect();
            const physics = global.Dts?.Physics;
            const state = global.Dts?.State;
            if (physics && state) {
                const pos = physics.screenToCanvas(e.clientX, e.clientY, rect);
                state.emit?.('click', pos.x, pos.y, e);
            }
        },

        // ===== BOX ZOOM OVERLAY =====

        /**
         * Check if box zoom is active
         */
        isBoxZoomActive() {
            return this._isBoxZoom;
        },

        /**
         * Get box zoom coordinates for drawing overlay
         */
        getBoxZoomRect() {
            return {
                x1: Math.min(this._boxStartX, this._boxEndX),
                y1: Math.min(this._boxStartY, this._boxEndY),
                x2: Math.max(this._boxStartX, this._boxEndX),
                y2: Math.max(this._boxStartY, this._boxEndY)
            };
        }
    };

    // Export to global namespace
    global.Dts = global.Dts || {};
    global.Dts.Events = DtsEvents;

})(window);
