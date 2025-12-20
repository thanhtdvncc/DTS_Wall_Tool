/**
 * DtsRenderer.js - Base Canvas Rendering
 * Provides canvas setup, transform application, and utility drawing.
 */
(function (global) {
    'use strict';

    const DtsRenderer = {
        // ===== CANVAS REFERENCE =====
        _canvas: null,
        _ctx: null,

        // ===== COLORS =====
        colors: {
            background: '#f8fafc',
            grid: '#e2e8f0',
            axis: '#94a3b8',
            selection: '#3b82f6',
            hover: '#dbeafe',
            boxZoom: 'rgba(59, 130, 246, 0.1)',
            boxZoomStroke: '#3b82f6'
        },

        /**
         * Initialize renderer with canvas element
         * @param {HTMLCanvasElement} canvas
         */
        init(canvas) {
            this._canvas = canvas;
            this._ctx = canvas.getContext('2d');
            return this;
        },

        /**
         * Get canvas context
         */
        getContext() {
            return this._ctx;
        },

        /**
         * Clear canvas with background color
         */
        clear(color) {
            const ctx = this._ctx;
            if (!ctx) return;
            ctx.fillStyle = color || this.colors.background;
            ctx.fillRect(0, 0, this._canvas.width, this._canvas.height);
        },

        /**
         * Begin transformed drawing (apply zoom and pan)
         */
        beginTransform() {
            const ctx = this._ctx;
            const state = global.Dts?.State || { zoom: 1, panX: 0, panY: 0 };

            ctx.save();
            ctx.translate(state.panX, state.panY);
            ctx.scale(state.zoom, state.zoom);
        },

        /**
         * End transformed drawing
         */
        endTransform() {
            this._ctx?.restore();
        },

        /**
         * Draw box zoom selection overlay (called after endTransform)
         */
        drawBoxZoomOverlay() {
            const events = global.Dts?.Events;
            if (!events?.isBoxZoomActive()) return;

            const ctx = this._ctx;
            const box = events.getBoxZoomRect();
            const w = box.x2 - box.x1;
            const h = box.y2 - box.y1;

            // Dashed rectangle
            ctx.strokeStyle = this.colors.boxZoomStroke;
            ctx.lineWidth = 2;
            ctx.setLineDash([5, 3]);
            ctx.strokeRect(box.x1, box.y1, w, h);
            ctx.setLineDash([]);

            // Fill
            ctx.fillStyle = this.colors.boxZoom;
            ctx.fillRect(box.x1, box.y1, w, h);
        },

        /**
         * Update zoom indicator element
         * @param {string} elementId - ID of zoom indicator element
         */
        updateZoomIndicator(elementId = 'zoomIndicator') {
            const el = document.getElementById(elementId);
            const state = global.Dts?.State;
            if (el && state) {
                el.textContent = `${Math.round(state.zoom * 100)}%`;
            }
        },

        /**
         * Resize canvas to fill container
         * @param {string} containerId - Container element ID
         */
        resizeToContainer(containerId) {
            const container = document.getElementById(containerId);
            if (!container || !this._canvas) return;

            this._canvas.style.width = '100%';
            this._canvas.style.height = '100%';
            this._canvas.height = container.clientHeight || 300;
            this._canvas.width = container.clientWidth || 800;
        }
    };

    // Export to global namespace
    global.Dts = global.Dts || {};
    global.Dts.Renderer = DtsRenderer;

})(window);
