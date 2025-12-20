/**
 * DtsHistory.js - Undo/Redo History Stack
 * Manages state snapshots for undo/redo functionality.
 */
(function (global) {
    'use strict';

    const DtsHistory = {
        // ===== STACK =====
        _stack: [],
        _pointer: -1,
        _maxSize: 30,

        // ===== METHODS =====

        /**
         * Push current state to history
         * @param {object} state - State to save (will be JSON stringified)
         * @returns {boolean} true if state was added (different from previous)
         */
        push(state) {
            const snapshot = JSON.stringify(state);

            // Skip if same as current
            if (this._pointer >= 0 && this._stack[this._pointer] === snapshot) {
                return false;
            }

            // Truncate future states if we're not at the end
            if (this._pointer < this._stack.length - 1) {
                this._stack = this._stack.slice(0, this._pointer + 1);
            }

            // Add new state
            this._stack.push(snapshot);
            this._pointer++;

            // Limit stack size
            if (this._stack.length > this._maxSize) {
                this._stack.shift();
                this._pointer--;
            }

            return true;
        },

        /**
         * Undo - go back one step
         * @returns {object|null} Previous state or null if at beginning
         */
        undo() {
            if (this._pointer > 0) {
                this._pointer--;
                return JSON.parse(this._stack[this._pointer]);
            }
            return null;
        },

        /**
         * Redo - go forward one step
         * @returns {object|null} Next state or null if at end
         */
        redo() {
            if (this._pointer < this._stack.length - 1) {
                this._pointer++;
                return JSON.parse(this._stack[this._pointer]);
            }
            return null;
        },

        /**
         * Check if undo is available
         */
        canUndo() {
            return this._pointer > 0;
        },

        /**
         * Check if redo is available
         */
        canRedo() {
            return this._pointer < this._stack.length - 1;
        },

        /**
         * Clear history
         */
        clear() {
            this._stack = [];
            this._pointer = -1;
        }
    };

    // Export to global namespace
    global.Dts = global.Dts || {};
    global.Dts.History = DtsHistory;

})(window);
