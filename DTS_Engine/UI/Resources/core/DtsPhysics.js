/**
 * DtsPhysics.js - Hit Testing and Coordinate Conversion
 * Provides canvas coordinate transformations and hit detection.
 */
(function (global) {
    'use strict';

    const DtsPhysics = {
        /**
         * Convert screen coordinates to canvas (world) coordinates
         * @param {number} screenX - Screen X position
         * @param {number} screenY - Screen Y position
         * @param {DOMRect} rect - Canvas bounding rect
         * @returns {{x: number, y: number}}
         */
        screenToCanvas(screenX, screenY, rect) {
            const state = global.Dts?.State || { zoom: 1, panX: 0, panY: 0 };
            return {
                x: (screenX - rect.left - state.panX) / state.zoom,
                y: (screenY - rect.top - state.panY) / state.zoom
            };
        },

        /**
         * Convert canvas (world) coordinates to screen coordinates
         * @param {number} canvasX - Canvas X position
         * @param {number} canvasY - Canvas Y position
         * @param {DOMRect} rect - Canvas bounding rect
         * @returns {{x: number, y: number}}
         */
        canvasToScreen(canvasX, canvasY, rect) {
            const state = global.Dts?.State || { zoom: 1, panX: 0, panY: 0 };
            return {
                x: canvasX * state.zoom + state.panX + rect.left,
                y: canvasY * state.zoom + state.panY + rect.top
            };
        },

        /**
         * Check if point is inside a rectangular bound
         * @param {number} x - Point X
         * @param {number} y - Point Y
         * @param {{x: number, y: number, width: number, height: number}} bound
         * @returns {boolean}
         */
        isPointInRect(x, y, bound) {
            return x >= bound.x &&
                x <= bound.x + bound.width &&
                y >= bound.y &&
                y <= bound.y + bound.height;
        },

        /**
         * Hit test against array of bounds
         * @param {number} x - Canvas X
         * @param {number} y - Canvas Y
         * @param {Array} bounds - Array of {x, y, width, height, index} objects
         * @returns {number} Index of hit item, or -1 if none
         */
        hitTest(x, y, bounds) {
            for (let i = 0; i < bounds.length; i++) {
                if (this.isPointInRect(x, y, bounds[i])) {
                    return bounds[i].index !== undefined ? bounds[i].index : i;
                }
            }
            return -1;
        }
    };

    // Export to global namespace
    global.Dts = global.Dts || {};
    global.Dts.Physics = DtsPhysics;

})(window);
