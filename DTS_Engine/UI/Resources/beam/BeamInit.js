/**
 * BeamInit.js - Beam Viewer Initialization
 * Bootstrap file that wires all modules together.
 */
(function (global) {
    'use strict';

    const BeamInit = {
        /**
         * Initialize the beam viewer with data from C#
         * @param {object} data - {mode, groups, settings}
         */
        init(data) {
            console.log('BeamInit: Starting initialization...');

            // Initialize core modules
            global.Dts?.UI?.init();

            // Get canvas element
            const canvas = document.getElementById('beamCanvas');
            if (!canvas) {
                console.error('BeamInit: Canvas element not found');
                return;
            }

            // Initialize renderer
            global.Dts?.Renderer?.init(canvas);
            global.Dts?.Renderer?.resizeToContainer('canvasContainer');

            // Initialize events with render callback
            global.Dts?.Events?.init(canvas, () => {
                global.Beam?.Renderer?.render();
            });

            // Initialize beam state with data
            global.Beam?.State?.init(data);

            // Populate UI
            global.Beam?.Actions?.populateOptionDropdown();
            global.Beam?.Actions?.updateMetrics();
            global.Beam?.Actions?.updateLockStatus();

            // Render
            global.Beam?.Renderer?.render();
            global.Beam?.Table?.render();

            // Window resize handler
            window.addEventListener('resize', () => {
                global.Dts?.Renderer?.resizeToContainer('canvasContainer');
                global.Beam?.Renderer?.render();
            });

            // Subscribe to state changes
            global.Dts?.State?.subscribe((eventType) => {
                if (eventType === 'group' || eventType === 'option') {
                    global.Beam?.Actions?.populateOptionDropdown();
                    global.Beam?.Actions?.updateMetrics();
                    global.Beam?.Actions?.updateLockStatus();
                }
                if (eventType === 'group' || eventType === 'option' || eventType === 'highlight') {
                    global.Beam?.Renderer?.render();
                    global.Beam?.Table?.render();
                }
            });

            console.log('BeamInit: Initialization complete');
            global.Dts?.UI?.showToast('✓ Viewer đã sẵn sàng', 'success', 1500);
        }
    };

    // Export to global namespace
    global.Beam = global.Beam || {};
    global.Beam.Init = BeamInit;

})(window);
