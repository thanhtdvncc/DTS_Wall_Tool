/**
 * BeamTable.js - Beam Data Table Rendering
 * Renders editable table of span rebar data.
 */
(function (global) {
    'use strict';

    const BeamTable = {
        /**
         * Render the span data table
         */
        render() {
            const tbody = document.getElementById('spanTableBody');
            if (!tbody) return;

            const beamState = global.Beam?.State;
            const spans = beamState?.currentGroup?.Spans || [];

            if (spans.length === 0) {
                tbody.innerHTML = '<tr><td colspan="8" class="text-center text-slate-400 py-4">Không có dữ liệu</td></tr>';
                return;
            }

            tbody.innerHTML = spans.map((span, i) => this._renderRow(span, i)).join('');
        },

        /**
         * Render a single table row
         */
        _renderRow(span, index) {
            const isHighlighted = index === global.Beam?.State?.highlightedSpanIndex;
            const rowClass = isHighlighted ? 'bg-blue-50' : (index % 2 ? 'bg-slate-50' : '');

            // Get rebar strings using structured RebarInfo if available
            let topRebar = '-';
            let botRebar = '-';

            if (span.TopBackbone) {
                topRebar = this._getMergedLabel(span.TopBackbone, span.TopAddLeft, span.TopAddRight);
            } else {
                topRebar = this._getLegacyRebarString(span.TopRebar);
            }

            if (span.BotBackbone) {
                botRebar = this._getMergedLabel(span.BotBackbone, span.BotAddMid);
            } else {
                botRebar = this._getLegacyRebarString(span.BotRebar);
            }

            const stirrup = span.Stirrup?.[1] || '-';

            return `
                <tr class="${rowClass} hover:bg-blue-100 cursor-pointer" 
                    data-span-index="${index}"
                    onclick="Beam.Table.onRowClick(${index})">
                    <td class="px-2 py-1 text-center font-bold">${span.SpanId || index + 1}</td>
                    <td class="px-2 py-1 text-center">${(span.Length || 0).toFixed(2)}</td>
                    <td class="px-2 py-1 text-center">${span.Width || 0}×${span.Height || 0}</td>
                    <td class="px-2 py-1 text-red-600">${topRebar}</td>
                    <td class="px-2 py-1 text-blue-600">${botRebar}</td>
                    <td class="px-2 py-1 text-center">${stirrup}</td>
                    <td class="px-2 py-1 text-center">${span.SideBar || '-'}</td>
                    <td class="px-2 py-1 text-center">
                        <button class="text-blue-500 hover:text-blue-700" 
                                onclick="event.stopPropagation(); Beam.Table.editSpan(${index})">
                            <i class="fa-solid fa-edit"></i>
                        </button>
                    </td>
                </tr>
            `;
        },

        _getMergedLabel(backbone, ...adds) {
            if (!backbone) return '-';

            // Find max add
            let maxAdd = null;
            let maxCount = 0;
            adds.forEach(a => {
                if (a && a.Count > maxCount) {
                    maxCount = a.Count;
                    maxAdd = a;
                }
            });

            if (!maxAdd) return backbone.DisplayString || '-';

            // Merge if same diameter
            if (backbone.Diameter === maxAdd.Diameter) {
                const total = backbone.Count + maxAdd.Count;
                return `${total}D${backbone.Diameter}`;
            }

            return `${backbone.DisplayString} + ${maxAdd.DisplayString}`;
        },

        /**
         * Legacy support for string arrays
         */
        _getLegacyRebarString(rebarArray) {
            if (!rebarArray || !Array.isArray(rebarArray)) return '-';
            const parts = [];
            for (let layer = 0; layer < rebarArray.length; layer++) {
                const layerData = rebarArray[layer];
                if (Array.isArray(layerData)) {
                    const val = layerData[0];
                    if (val && typeof val === 'string') parts.push(val);
                }
            }
            return parts.length > 0 ? parts.join(' + ') : '-';
        },

        /**
         * Handle row click
         */
        onRowClick(index) {
            global.Beam?.State?.highlightSpan(index);
            global.Beam?.Renderer?.render();
            this.render();
        },

        /**
         * Edit span (open modal or inline edit)
         */
        editSpan(index) {
            const beamState = global.Beam?.State;
            const span = beamState?.currentGroup?.Spans?.[index];
            if (!span) return;

            // For now, just highlight and show toast
            beamState.highlightSpan(index);
            global.Dts?.UI?.showToast(`Editing ${span.SpanId}`, 'info');

            // TODO: Open edit modal
        },

        /**
         * Scroll to highlighted row
         */
        scrollToHighlighted() {
            const index = global.Beam?.State?.highlightedSpanIndex;
            if (index < 0) return;

            const row = document.querySelector(`[data-span-index="${index}"]`);
            row?.scrollIntoView({ behavior: 'smooth', block: 'center' });
        }
    };

    // Export to global namespace
    global.Beam = global.Beam || {};
    global.Beam.Table = BeamTable;

})(window);
