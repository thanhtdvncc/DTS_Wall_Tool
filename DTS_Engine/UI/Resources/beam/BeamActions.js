/**
 * BeamActions.js - Beam User Actions
 * [CLEANUP] Removed redundant UI logic now handled in BeamGroupViewer.html
 */
(function (global) {
    'use strict';

    const BeamActions = {
        // [UNIQUE] Trigger rebar editing in C# (WebView2)
        editRebar(spanIndex, position) {
            const beamState = global.Beam?.State;
            if (beamState) {
                // Format: EDIT_REBAR|{"GroupIndex":..., "SpanIndex":..., "Position":"top"/"bot"}
                const payload = {
                    GroupIndex: beamState.currentGroupIndex,
                    SpanIndex: spanIndex,
                    Position: position
                };
                this.sendToHost('EDIT_REBAR', payload);
                console.log('Sent EDIT_REBAR:', payload);
            }
        },

        // [UNIQUE] Show detailed calculation report
        showCalculationReport(spanIndex = -1) {
            const beamState = global.Beam?.State;
            if (beamState) {
                const payload = {
                    GroupIndex: beamState.currentGroupIndex,
                    SpanIndex: spanIndex
                };
                this.sendToHost('SHOW_REPORT', payload);
            }
        },

        // Trigger full data package save
        save() {
            if (global.doSave) {
                global.doSave();
            } else {
                this.sendToHost('SAVE', global.data);
            }
        },

        // Helper to send messages to C# host
        sendToHost(action, data) {
            if (window.chrome?.webview?.postMessage) {
                const payload = (typeof data === 'string') ? data : JSON.stringify(data);
                window.chrome.webview.postMessage(`${action}|${payload}`);
            }
        },

        // Utility: Show toast via centralized UI or fallback
        showToast(message, type = 'info') {
            if (global.showToast) {
                global.showToast(message);
            } else if (global.Dts?.UI?.showToast) {
                global.Dts.UI.showToast(message, type);
            } else {
                console.log(`[Toast] ${type}: ${message}`);
            }
        }
    };

    global.Beam = global.Beam || {};
    global.Beam.Actions = BeamActions;

})(window);
