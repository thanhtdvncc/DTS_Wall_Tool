/**
 * DtsState.js - Core State Management
 * Manages application data, zoom, pan, selection, and change notifications.
 */
(function (global) {
    'use strict';

    const DtsState = {
        // ===== DATA =====
        data: null,

        // ===== CANVAS STATE =====
        zoom: 1.0,
        panX: 0,
        panY: 0,

        // ===== SELECTION =====
        selectedIndex: -1,
        hoveredIndex: -1,

        // ===== LISTENERS =====
        _listeners: [],

        // ===== METHODS =====

        /**
         * Set main data object
         */
        setData(data) {
            this.data = data;
            this.notify('data');
        },

        /**
         * Subscribe to state changes
         */
        subscribe(callback) {
            if (typeof callback === 'function') {
                this._listeners.push(callback);
            }
            return () => {
                this._listeners = this._listeners.filter(fn => fn !== callback);
            };
        },

        /**
         * Notify all listeners of state change
         */
        notify(eventType = 'change') {
            this._listeners.forEach(fn => {
                try { fn(eventType, this); } catch (e) { console.error('DtsState listener error:', e); }
            });
        },

        /**
         * Reset view to default
         */
        resetView() {
            this.zoom = 1.0;
            this.panX = 0;
            this.panY = 0;
            this.notify('view');
        },

        /**
         * Set selection
         */
        setSelection(index) {
            this.selectedIndex = index;
            this.notify('selection');
        },

        /**
         * Set hover
         */
        setHover(index) {
            if (this.hoveredIndex !== index) {
                this.hoveredIndex = index;
                this.notify('hover');
            }
        }
    };

    // Export to global namespace
    global.Dts = global.Dts || {};
    global.Dts.State = DtsState;

})(window);
