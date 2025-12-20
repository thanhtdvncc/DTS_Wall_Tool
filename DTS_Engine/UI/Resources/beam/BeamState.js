/**
 * BeamState.js - Beam-Specific State Management
 * Extends Dts.State with beam group data and options.
 */
(function (global) {
    'use strict';

    const BeamState = {
        // ===== BEAM DATA =====
        currentGroupIndex: 0,
        currentGroup: null,
        groups: [],
        settings: {},

        // ===== OPTION SELECTION =====
        selectedOptionKey: null,  // 'locked' | numeric index as string

        // ===== SPAN BOUNDS (for hit testing) =====
        spanBounds: [],

        // ===== UI FLAGS =====
        highlightedSpanIndex: -1,
        canvasMode: 'long',  // 'section' | 'long' | 'shear'

        /**
         * Initialize with data from C#
         */
        init(data) {
            this.groups = data.groups || [];
            this.settings = data.settings || {};

            // Also initialize core state
            if (global.Dts?.State) {
                global.Dts.State.setData(data);
            }

            // Load first group if available
            if (this.groups.length > 0) {
                this.loadGroup(0);
            }
        },

        /**
         * Load a specific group by index
         */
        loadGroup(index) {
            if (index < 0 || index >= this.groups.length) return;

            this.currentGroupIndex = index;
            this.currentGroup = this.groups[index];
            this.selectedOptionKey = null;  // Reset selection
            this.highlightedSpanIndex = -1;
            this.spanBounds = [];

            // Determine initial option selection
            if (this.currentGroup?.SelectedDesign) {
                this.selectedOptionKey = 'locked';
            } else if (this.currentGroup?.BackboneOptions?.length > 0) {
                this.selectedOptionKey = String(this.currentGroup.SelectedBackboneIndex || 0);
            }

            // Notify listeners
            global.Dts?.State?.notify('group');
        },

        /**
         * Navigate to previous group
         */
        prevGroup() {
            if (this.currentGroupIndex > 0) {
                this.loadGroup(this.currentGroupIndex - 1);
            }
        },

        /**
         * Navigate to next group
         */
        nextGroup() {
            if (this.currentGroupIndex < this.groups.length - 1) {
                this.loadGroup(this.currentGroupIndex + 1);
            }
        },

        /**
         * Get currently selected design option
         */
        getSelectedOption() {
            if (!this.currentGroup) return null;

            if (this.selectedOptionKey === 'locked') {
                return this.currentGroup.SelectedDesign || null;
            }

            const idx = parseInt(this.selectedOptionKey);
            if (isNaN(idx)) {
                return this.currentGroup.BackboneOptions?.[0] || null;
            }
            return this.currentGroup.BackboneOptions?.[idx] || null;
        },

        /**
         * Select a design option
         */
        selectOption(key) {
            this.selectedOptionKey = String(key);

            if (key !== 'locked') {
                const parsed = parseInt(key);
                if (!isNaN(parsed) && this.currentGroup) {
                    this.currentGroup.SelectedBackboneIndex = parsed;
                }
            }

            global.Dts?.State?.notify('option');
        },

        /**
         * Set canvas display mode
         */
        setCanvasMode(mode) {
            this.canvasMode = mode;
            global.Dts?.State?.notify('mode');
        },

        /**
         * Highlight a span
         */
        highlightSpan(index) {
            this.highlightedSpanIndex = index;
            global.Dts?.State?.notify('highlight');
        }
    };

    // Export to global namespace
    global.Beam = global.Beam || {};
    global.Beam.State = BeamState;

})(window);
