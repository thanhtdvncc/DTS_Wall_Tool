/**
 * BeamActions.js - Beam User Actions
 * Handles dropdown, lock design, export, and messaging to C#.
 */
(function (global) {
    'use strict';

    const BeamActions = {
        /**
         * Populate the option dropdown
         */
        populateOptionDropdown() {
            const sel = document.getElementById('optionSelect');
            if (!sel) return;

            const beamState = global.Beam?.State;
            const group = beamState?.currentGroup;
            const options = [];

            // Locked design (if exists)
            if (group?.SelectedDesign) {
                const isInvalid = group.SelectedDesign.IsValid === false;
                const name = group.SelectedDesign.OptionName || 'Phương án đã chốt';
                options.push({
                    value: 'locked',
                    text: `⭐ ${name} ${isInvalid ? '⚠️ THIẾU THÉP' : '(Đang dùng)'}`,
                    selected: beamState.selectedOptionKey === 'locked'
                });
            }

            // Backbone options
            const backboneOpts = group?.BackboneOptions || [];
            if (backboneOpts.length === 0) {
                options.push({
                    value: 'none',
                    text: '⏳ Chưa tính (Nhấn "Tính thép")',
                    disabled: true
                });
            } else {
                backboneOpts.forEach((opt, i) => {
                    options.push({
                        value: String(i),
                        text: `${opt.OptionName || 'Option ' + (i + 1)} - D${opt.BackboneDiameter || '?'}${i === 0 ? ' ★Best' : ''}`,
                        selected: beamState?.selectedOptionKey === String(i)
                    });
                });
            }

            // Use DtsUI to populate
            global.Dts?.UI?.populateDropdown(sel, options);
        },

        /**
         * Handle option selection change
         */
        onOptionSelect(value) {
            global.Beam?.State?.selectOption(value);
            this.applyOptionToSpans();
            this.updateMetrics();
            global.Beam?.Renderer?.render();
            global.Beam?.Table?.render();
        },

        /**
         * Apply selected option to spans
         */
        applyOptionToSpans() {
            const beamState = global.Beam?.State;
            const group = beamState?.currentGroup;
            const opt = beamState?.getSelectedOption();

            if (!group?.Spans?.length || !opt) return;

            const backbone = `${opt.BackboneCount_Top}D${opt.BackboneDiameter}`;
            const botBackbone = `${opt.BackboneCount_Bot}D${opt.BackboneDiameter}`;

            group.Spans.forEach(span => {
                if (!span._userEdited) {
                    if (!span.TopRebar) span.TopRebar = [[], [], []];
                    if (!span.BotRebar) span.BotRebar = [[], [], []];
                    span.TopRebar[0][0] = backbone;
                    span.BotRebar[0][0] = botBackbone;
                }
            });
        },

        /**
         * Update metrics display
         */
        updateMetrics() {
            const opt = global.Beam?.State?.getSelectedOption();
            if (!opt) return;

            // Update metric elements
            const setEl = (id, val) => {
                const el = document.getElementById(id);
                if (el) el.textContent = val;
            };

            setEl('metricBackbone', `${opt.BackboneCount_Top}D${opt.BackboneDiameter}`);
            setEl('metricWeight', opt.TotalSteelWeight ? `${opt.TotalSteelWeight.toFixed(1)} kg` : '-');
            setEl('metricScore', opt.TotalScore ? `${opt.TotalScore.toFixed(0)}/100` : '-');
        },

        /**
         * Lock current design
         */
        lockDesign() {
            const beamState = global.Beam?.State;
            const group = beamState?.currentGroup;
            if (!group) return;

            const opt = beamState.getSelectedOption();
            if (!opt) {
                global.Dts?.UI?.showToast('Chưa có phương án để chốt', 'warning');
                return;
            }

            // Copy option to SelectedDesign
            group.SelectedDesign = JSON.parse(JSON.stringify(opt));
            group.LockedAt = new Date().toISOString();
            beamState.selectedOptionKey = 'locked';

            this.populateOptionDropdown();
            this.updateLockStatus();
            global.Dts?.UI?.showToast('✓ Đã chốt phương án', 'success');

            // Notify C# if available
            this.sendToHost('LOCK', { groupIndex: beamState.currentGroupIndex });
        },

        /**
         * Update lock status display
         */
        updateLockStatus() {
            const group = global.Beam?.State?.currentGroup;
            const lockBtn = document.getElementById('lockBtn');
            const lockStatus = document.getElementById('lockStatus');

            if (group?.SelectedDesign) {
                lockBtn?.classList.add('hidden');
                lockStatus?.classList.remove('hidden');
            } else {
                lockBtn?.classList.remove('hidden');
                lockStatus?.classList.add('hidden');
            }
        },

        /**
         * Send message to C# host (using postMessage pattern)
         */
        sendToHost(action, data) {
            if (window.chrome?.webview?.postMessage) {
                window.chrome.webview.postMessage(`${action}|${JSON.stringify(data)}`);
            }
        },

        /**
         * Save current data
         */
        save() {
            const data = {
                groups: global.Beam?.State?.groups || []
            };
            this.sendToHost('SAVE', data);
            global.Dts?.UI?.showToast('✓ Đã lưu', 'success');
        }
    };

    // Export to global namespace
    global.Beam = global.Beam || {};
    global.Beam.Actions = BeamActions;

})(window);
