/**
 * BeamRenderer.js - Beam Canvas Rendering
 * Draws beam spans, supports, rebar, and dimensions.
 */
(function (global) {
    'use strict';

    const BeamRenderer = {
        // ===== CONSTANTS =====
        BEAM_HEIGHT: 80,
        MIN_SPAN_WIDTH: 80,
        MAX_CANVAS_WIDTH: 1600,
        CANVAS_PADDING: 30,
        SUPPORT_GAP: 15,

        // ===== COLORS =====
        colors: {
            beamFill: '#e2e8f0',
            beamStroke: '#64748b',
            highlightFill: '#dbeafe',
            highlightStroke: '#3b82f6',
            rebarTop: '#dc2626',
            rebarBot: '#2563eb',
            dimension: '#64748b',
            label: '#1e293b'
        },

        /**
         * Main render function
         */
        render() {
            const canvas = document.getElementById('beamCanvas');
            const ctx = canvas?.getContext('2d');
            if (!ctx) return;

            const beamState = global.Beam?.State;
            const group = beamState?.currentGroup;

            // No data - show message
            if (!group?.Spans?.length) {
                this._drawNoData(ctx, canvas);
                return;
            }

            // Calculate span widths
            const spans = group.Spans;
            const widths = this._calculateSpanWidths(spans, canvas.width);

            // Clear and setup
            global.Dts?.Renderer?.clear();
            global.Dts?.Renderer?.beginTransform();

            // Draw beams
            let x = this.CANVAS_PADDING;
            const beamY = 60;
            beamState.spanBounds = [];

            spans.forEach((span, i) => {
                const w = widths[i];

                // Left support (first span only)
                if (i === 0) {
                    this._drawSupport(ctx, x, beamY);
                    x += 5;
                }

                // Store bounds for hit testing
                beamState.spanBounds.push({
                    x, y: beamY,
                    width: w, height: this.BEAM_HEIGHT,
                    index: i
                });

                // Draw span
                const isHighlighted = i === beamState.highlightedSpanIndex;
                this._drawSpan(ctx, x, beamY, w, span, isHighlighted);

                // Draw rebar
                this._drawRebar(ctx, x, beamY, w, span);

                // Labels and dimensions
                this._drawLabels(ctx, x, beamY, w, span);

                x += w;

                // Support between spans
                this._drawSupport(ctx, x, beamY);
                x += this.SUPPORT_GAP;
            });

            // End transform
            global.Dts?.Renderer?.endTransform();

            // Draw overlays (box zoom, etc)
            global.Dts?.Renderer?.drawBoxZoomOverlay();
            global.Dts?.Renderer?.updateZoomIndicator();
        },

        /**
         * Calculate proportional span widths
         */
        _calculateSpanWidths(spans, canvasWidth) {
            const lengths = spans.map(s => s.Length || 1);
            const maxLen = Math.max(...lengths);

            const maxSpanWidth = Math.min(200, (this.MAX_CANVAS_WIDTH - this.CANVAS_PADDING * 2 - spans.length * this.SUPPORT_GAP) / Math.max(1, spans.length));
            const minSpanWidth = Math.max(this.MIN_SPAN_WIDTH, maxSpanWidth / 5);

            return lengths.map(len => {
                const ratio = len / maxLen;
                return minSpanWidth + (maxSpanWidth - minSpanWidth) * ratio;
            });
        },

        /**
         * Draw "no data" message
         */
        _drawNoData(ctx, canvas) {
            canvas.width = 400;
            ctx.fillStyle = '#f8fafc';
            ctx.fillRect(0, 0, 400, 180);
            ctx.fillStyle = '#94a3b8';
            ctx.font = '14px sans-serif';
            ctx.textAlign = 'center';
            ctx.fillText('Không có dữ liệu nhịp', 200, 90);
        },

        /**
         * Draw beam span rectangle
         */
        _drawSpan(ctx, x, y, w, span, isHighlighted) {
            ctx.fillStyle = isHighlighted ? this.colors.highlightFill : this.colors.beamFill;
            ctx.strokeStyle = isHighlighted ? this.colors.highlightStroke : this.colors.beamStroke;
            ctx.lineWidth = isHighlighted ? 2 : 1;
            ctx.fillRect(x, y, w, this.BEAM_HEIGHT);
            ctx.strokeRect(x, y, w, this.BEAM_HEIGHT);
        },

        /**
         * Draw support symbol (triangle)
         */
        _drawSupport(ctx, x, y) {
            const h = 15;
            ctx.beginPath();
            ctx.moveTo(x, y + this.BEAM_HEIGHT);
            ctx.lineTo(x - 6, y + this.BEAM_HEIGHT + h);
            ctx.lineTo(x + 6, y + this.BEAM_HEIGHT + h);
            ctx.closePath();
            ctx.fillStyle = '#475569';
            ctx.fill();
        },

        /**
         * Draw rebar lines (N-layers, Backbone + Add)
         */
        _drawRebar(ctx, x, y, w, span) {
            const topY = y + 6;
            const botY = y + this.BEAM_HEIGHT - 6;
            const layerOffset = 4; // Distance between layers

            // --- TOP REBAR ---
            // 1. Backbone (Run full length)
            if (span.TopBackbone) {
                this._drawRebarGroup(ctx, x, topY, w, span.TopBackbone, 'top', 0);
            }

            // 2. Additional (Left, Mid, Right)
            // Stacking: If backbone exists, add sits below it (higher Y)
            let baseLayer = span.TopBackbone ? (span.TopBackbone.LayerCounts?.length || 1) : 0;

            if (span.TopAddLeft) {
                // Draw items Left (0 to 0.25L)
                this._drawRebarGroup(ctx, x, topY, w * 0.25, span.TopAddLeft, 'top', baseLayer);
            }
            if (span.TopAddRight) {
                // Draw items Right (0.75L to L)
                this._drawRebarGroup(ctx, x + w * 0.75, topY, w * 0.25, span.TopAddRight, 'top', baseLayer);
            }

            // --- BOT REBAR ---
            // 1. Backbone
            if (span.BotBackbone) {
                this._drawRebarGroup(ctx, x, botY, w, span.BotBackbone, 'bot', 0);
            }
            // 2. Additional Mid
            baseLayer = span.BotBackbone ? (span.BotBackbone.LayerCounts?.length || 1) : 0;
            if (span.BotAddMid) {
                // Draw items Mid (0.15L to 0.85L)
                this._drawRebarGroup(ctx, x + w * 0.15, botY, w * 0.7, span.BotAddMid, 'bot', baseLayer);
            }
        },

        _drawRebarGroup(ctx, startX, startY, length, info, pos, startLayer) {
            const isTop = pos === 'top';
            const color = isTop ? this.colors.rebarTop : this.colors.rebarBot;
            ctx.strokeStyle = color;
            ctx.lineWidth = 2; // Fixed thickness for now

            const layers = info.LayerCounts || [info.Count];

            layers.forEach((count, idx) => {
                if (count <= 0) return;
                const layerIdx = startLayer + idx;
                const offset = layerIdx * 4 * (isTop ? 1 : -1); // Top goes down (+), Bot goes up (-)

                ctx.beginPath();
                ctx.moveTo(startX, startY + offset);
                ctx.lineTo(startX + length, startY + offset);
                ctx.stroke();
            });
        },

        /**
         * Draw span labels and dimensions
         */
        _drawLabels(ctx, x, y, w, span) {
            // Span ID
            ctx.fillStyle = this.colors.label;
            ctx.font = 'bold 11px sans-serif';
            ctx.textAlign = 'center';
            ctx.textBaseline = 'middle';
            ctx.fillText(span.SpanId || '', x + w / 2, y + this.BEAM_HEIGHT / 2);

            // Rebar Labels based on RebarInfo
            ctx.font = '10px sans-serif';

            // Top Label (Max of Left/Mid/Right)
            const topLabel = this._getMergedLabel(span.TopBackbone, span.TopAddLeft, span.TopAddRight);
            ctx.fillStyle = this.colors.rebarTop;
            ctx.fillText(topLabel, x + w / 2, y - 12);

            // Bot Label
            const botLabel = this._getMergedLabel(span.BotBackbone, span.BotAddMid);
            ctx.fillStyle = this.colors.rebarBot;
            ctx.fillText(botLabel, x + w / 2, y + this.BEAM_HEIGHT + 24);

            // Length dimension
            ctx.fillStyle = this.colors.dimension;
            ctx.fillText(`${(span.Length || 0).toFixed(2)}m`, x + w / 2, y + this.BEAM_HEIGHT + 12);

            // Section size
            ctx.fillText(`${span.Width || 0}×${span.Height || 0}`, x + w / 2, y - 2);
        },

        _getMergedLabel(backbone, ...adds) {
            if (!backbone) return '';

            // Find max add to combine with
            let maxAdd = null;
            let maxCount = 0;
            adds.forEach(a => {
                if (a && a.Count > maxCount) {
                    maxCount = a.Count;
                    maxAdd = a;
                }
            });

            if (!maxAdd) return backbone.DisplayString || '';

            // Merge if same diameter
            if (backbone.Diameter === maxAdd.Diameter) {
                const total = backbone.Count + maxAdd.Count;
                return `${total}D${backbone.Diameter}`;
            }

            // Otherwise concat
            return `${backbone.DisplayString} + ${maxAdd.DisplayString}`;
        }
    };

    // Export to global namespace
    global.Beam = global.Beam || {};
    global.Beam.Renderer = BeamRenderer;

})(window);
