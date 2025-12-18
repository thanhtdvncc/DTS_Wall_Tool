/**
 * BeamCanvas.js - Canvas Interaction Module for BeamGroupViewer
 * Extracted and adapted from DTS_Engine/UI/Sample/index.html
 * 
 * Provides:
 * - Canvas state management (zoom, pan, selection)
 * - Mouse interaction handlers (click, drag, hover, wheel zoom)
 * - Element rendering (spans, supports, rebar)
 * - Coordinate transformation utilities
 */

// ==================== STATE MODULE ====================
const BeamState = {
    // Canvas state
    zoom: 1.0,
    panX: 0,
    panY: 0,

    // Selection state
    selectedSpanIndex: -1,
    hoveredSpanIndex: -1,

    // Display mode
    canvasMode: 'rebar', // 'rebar' | 'section'

    // Data reference
    currentGroup: null,
    spanBounds: [],

    // Canvas reference
    canvas: null,
    ctx: null,

    // Listeners
    listeners: [],

    subscribe(fn) {
        this.listeners.push(fn);
    },

    notify() {
        this.listeners.forEach(fn => fn());
    },

    setSelection(index) {
        this.selectedSpanIndex = index;
        this.notify();
    },

    setHover(index) {
        if (this.hoveredSpanIndex !== index) {
            this.hoveredSpanIndex = index;
            this.notify();
        }
    },

    setMode(mode) {
        this.canvasMode = mode;
        BeamRenderer.draw();
    },

    setData(group) {
        this.currentGroup = group;
        BeamRenderer.draw();
    }
};

// ==================== PHYSICS MODULE ====================
const BeamPhysics = {
    // Check if point is inside a span bound
    hitTest(x, y) {
        for (let i = 0; i < BeamState.spanBounds.length; i++) {
            const b = BeamState.spanBounds[i];
            if (x >= b.x && x <= b.x + b.width &&
                y >= b.y && y <= b.y + b.height) {
                return i;
            }
        }
        return -1;
    },

    // Convert screen coords to canvas coords (accounting for zoom/pan)
    screenToCanvas(screenX, screenY) {
        const rect = BeamState.canvas.getBoundingClientRect();
        return {
            x: (screenX - rect.left - BeamState.panX) / BeamState.zoom,
            y: (screenY - rect.top - BeamState.panY) / BeamState.zoom
        };
    },

    // Convert canvas coords to screen coords
    canvasToScreen(canvasX, canvasY) {
        const rect = BeamState.canvas.getBoundingClientRect();
        return {
            x: canvasX * BeamState.zoom + BeamState.panX + rect.left,
            y: canvasY * BeamState.zoom + BeamState.panY + rect.top
        };
    }
};

// ==================== RENDERER MODULE ====================
const BeamRenderer = {
    // Constants
    BEAM_HEIGHT: 80,
    MIN_SPAN_WIDTH: 80,
    MAX_CANVAS_WIDTH: 1600,
    CANVAS_PADDING: 30,
    SUPPORT_HEIGHT: 15,

    // Initialize canvas
    init(canvasElement) {
        BeamState.canvas = canvasElement;
        BeamState.ctx = canvasElement.getContext('2d');

        // Setup event handlers
        BeamEvents.attach(canvasElement);

        // Subscribe to state changes
        BeamState.subscribe(() => this.draw());
    },

    // Main draw function
    draw() {
        const canvas = BeamState.canvas;
        const ctx = BeamState.ctx;
        const group = BeamState.currentGroup;

        if (!canvas || !ctx) return;

        if (!group?.Spans?.length) {
            canvas.width = 400;
            ctx.fillStyle = '#f8fafc';
            ctx.fillRect(0, 0, 400, 200);
            ctx.fillStyle = '#94a3b8';
            ctx.font = '14px sans-serif';
            ctx.textAlign = 'center';
            ctx.fillText('Không có dữ liệu nhịp', 200, 100);
            return;
        }

        BeamState.spanBounds = [];
        const spans = group.Spans;

        // Calculate widths
        const lengths = spans.map(s => s.Length || 1);
        const availableWidth = this.MAX_CANVAS_WIDTH - this.CANVAS_PADDING * 2 - spans.length * 10;
        const totalLen = lengths.reduce((a, b) => a + b, 0);

        let totalWidth = this.CANVAS_PADDING * 2;
        const spanWidths = lengths.map(len => {
            const ratio = len / totalLen;
            const w = Math.max(this.MIN_SPAN_WIDTH, ratio * availableWidth);
            totalWidth += w + 10;
            return w;
        });

        canvas.width = Math.min(totalWidth, this.MAX_CANVAS_WIDTH);

        // Apply zoom transform
        ctx.save();
        ctx.translate(BeamState.panX, BeamState.panY);
        ctx.scale(BeamState.zoom, BeamState.zoom);

        // Clear background
        ctx.fillStyle = '#f8fafc';
        ctx.fillRect(0, 0, canvas.width / BeamState.zoom, 200);

        let x = this.CANVAS_PADDING;
        const beamY = 60;

        // Draw spans
        spans.forEach((span, i) => {
            const w = spanWidths[i];

            // Support at start
            if (i === 0) {
                this.drawSupport(ctx, x, beamY);
                x += 5;
            }

            // Store bounds for hit testing
            BeamState.spanBounds.push({
                x: x,
                y: beamY,
                width: w,
                height: this.BEAM_HEIGHT,
                index: i
            });

            // Draw span
            const isSelected = i === BeamState.selectedSpanIndex;
            const isHovered = i === BeamState.hoveredSpanIndex;

            ctx.fillStyle = isSelected ? '#dbeafe' : (isHovered ? '#f1f5f9' : '#e2e8f0');
            ctx.strokeStyle = isSelected ? '#3b82f6' : (isHovered ? '#94a3b8' : '#64748b');
            ctx.lineWidth = isSelected ? 2 : 1;
            ctx.fillRect(x, beamY, w, this.BEAM_HEIGHT);
            ctx.strokeRect(x, beamY, w, this.BEAM_HEIGHT);

            // Draw rebar
            this.drawRebar(ctx, x, beamY, w, span);

            // Span label
            ctx.fillStyle = '#1e293b';
            ctx.font = 'bold 11px sans-serif';
            ctx.textAlign = 'center';
            ctx.fillText(span.SpanId, x + w / 2, beamY + this.BEAM_HEIGHT / 2 + 4);

            // Dimension
            ctx.fillStyle = '#64748b';
            ctx.font = '10px sans-serif';
            ctx.fillText(`${(span.Length || 0).toFixed(2)}m`, x + w / 2, beamY + this.BEAM_HEIGHT + 12);
            ctx.fillText(`${span.Width || 0}×${span.Height || 0}`, x + w / 2, beamY - 5);

            x += w;

            // Support after span
            this.drawSupport(ctx, x, beamY);
            x += 10;
        });

        ctx.restore();
    },

    // Draw support symbol (triangle)
    drawSupport(ctx, x, y) {
        ctx.fillStyle = '#475569';
        ctx.beginPath();
        ctx.moveTo(x, y + this.BEAM_HEIGHT);
        ctx.lineTo(x - 6, y + this.BEAM_HEIGHT + this.SUPPORT_HEIGHT);
        ctx.lineTo(x + 6, y + this.BEAM_HEIGHT + this.SUPPORT_HEIGHT);
        ctx.closePath();
        ctx.fill();
    },

    // Draw rebar lines and labels
    drawRebar(ctx, x, y, w, span) {
        const topY = y + 12;
        const botY = y + this.BEAM_HEIGHT - 12;

        // Top backbone (red)
        ctx.strokeStyle = '#ef4444';
        ctx.lineWidth = 2;
        ctx.setLineDash([]);
        ctx.beginPath();
        ctx.moveTo(x + 3, topY);
        ctx.lineTo(x + w - 3, topY);
        ctx.stroke();

        // Bottom backbone (blue)
        ctx.strokeStyle = '#3b82f6';
        ctx.beginPath();
        ctx.moveTo(x + 3, botY);
        ctx.lineTo(x + w - 3, botY);
        ctx.stroke();

        // Rebar mode - show values at 6 positions
        if (BeamState.canvasMode === 'rebar') {
            ctx.font = 'bold 9px sans-serif';
            ctx.textAlign = 'center';

            const positions = [
                { pos: 'L', xOff: 0.15 },
                { pos: 'M', xOff: 0.50 },
                { pos: 'R', xOff: 0.85 }
            ];

            // Top values
            ctx.fillStyle = '#dc2626';
            positions.forEach(p => {
                const pX = x + w * p.xOff;
                const rebarText = span.TopRebar?.[0]?.[p.pos === 'L' ? 0 : p.pos === 'M' ? 2 : 4] || '';
                if (rebarText) {
                    ctx.beginPath();
                    ctx.arc(pX, topY, 3, 0, Math.PI * 2);
                    ctx.fill();
                    ctx.fillText(rebarText, pX, topY - 8);
                }
            });

            // Bottom values
            ctx.fillStyle = '#2563eb';
            positions.forEach(p => {
                const pX = x + w * p.xOff;
                const rebarText = span.BotRebar?.[0]?.[p.pos === 'L' ? 0 : p.pos === 'M' ? 2 : 4] || '';
                if (rebarText) {
                    ctx.beginPath();
                    ctx.arc(pX, botY, 3, 0, Math.PI * 2);
                    ctx.fill();
                    ctx.fillText(rebarText, pX, botY + 14);
                }
            });
        }

        ctx.setLineDash([]);
    },

    // Scroll to selected span
    scrollToSpan(index) {
        if (index >= 0 && BeamState.spanBounds[index]) {
            const bound = BeamState.spanBounds[index];
            const container = BeamState.canvas.parentElement;
            if (container) {
                const scrollTarget = bound.x - container.clientWidth / 2 + bound.width / 2;
                container.scrollTo({ left: scrollTarget, behavior: 'smooth' });
            }
        }
    }
};

// ==================== EVENTS MODULE ====================
const BeamEvents = {
    isDragging: false,
    lastX: 0,
    lastY: 0,

    attach(canvas) {
        // Mouse move - hover detection
        canvas.addEventListener('mousemove', (e) => {
            if (this.isDragging) {
                // Pan mode
                const dx = e.clientX - this.lastX;
                const dy = e.clientY - this.lastY;
                BeamState.panX += dx;
                BeamState.panY += dy;
                this.lastX = e.clientX;
                this.lastY = e.clientY;
                BeamRenderer.draw();
            } else {
                // Hover detection
                const pos = BeamPhysics.screenToCanvas(e.clientX, e.clientY);
                const hitIndex = BeamPhysics.hitTest(pos.x, pos.y);
                BeamState.setHover(hitIndex);
            }
        });

        // Mouse down - start drag or select
        canvas.addEventListener('mousedown', (e) => {
            if (e.button === 1 || (e.button === 0 && e.altKey)) {
                // Middle click or Alt+Left = pan
                this.isDragging = true;
                this.lastX = e.clientX;
                this.lastY = e.clientY;
                canvas.style.cursor = 'grabbing';
            } else if (e.button === 0) {
                // Left click = select
                const pos = BeamPhysics.screenToCanvas(e.clientX, e.clientY);
                const hitIndex = BeamPhysics.hitTest(pos.x, pos.y);
                if (hitIndex >= 0) {
                    BeamState.setSelection(hitIndex);
                    // Trigger external callback if set
                    if (typeof window.onSpanSelect === 'function') {
                        window.onSpanSelect(hitIndex);
                    }
                }
            }
        });

        // Mouse up - end drag
        canvas.addEventListener('mouseup', () => {
            this.isDragging = false;
            canvas.style.cursor = 'default';
        });

        // Mouse leave
        canvas.addEventListener('mouseleave', () => {
            this.isDragging = false;
            BeamState.setHover(-1);
            canvas.style.cursor = 'default';
        });

        // Wheel - zoom
        canvas.addEventListener('wheel', (e) => {
            e.preventDefault();
            const delta = e.deltaY > 0 ? 0.9 : 1.1;
            const newZoom = Math.max(0.5, Math.min(3, BeamState.zoom * delta));

            // Zoom towards mouse position
            const rect = canvas.getBoundingClientRect();
            const mouseX = e.clientX - rect.left;
            const mouseY = e.clientY - rect.top;

            BeamState.panX = mouseX - (mouseX - BeamState.panX) * (newZoom / BeamState.zoom);
            BeamState.panY = mouseY - (mouseY - BeamState.panY) * (newZoom / BeamState.zoom);
            BeamState.zoom = newZoom;

            BeamRenderer.draw();
        }, { passive: false });

        // Double click - reset zoom
        canvas.addEventListener('dblclick', () => {
            BeamState.zoom = 1.0;
            BeamState.panX = 0;
            BeamState.panY = 0;
            BeamRenderer.draw();
        });
    }
};

// ==================== PUBLIC API ====================
const BeamCanvas = {
    // Initialize module with canvas element
    init(canvasId) {
        const canvas = document.getElementById(canvasId);
        if (canvas) {
            BeamRenderer.init(canvas);
        }
        return this;
    },

    // Set beam group data
    setData(group) {
        BeamState.setData(group);
        return this;
    },

    // Set display mode
    setMode(mode) {
        BeamState.setMode(mode);
        return this;
    },

    // Get current selection
    getSelection() {
        return BeamState.selectedSpanIndex;
    },

    // Set selection programmatically
    select(index) {
        BeamState.setSelection(index);
        BeamRenderer.scrollToSpan(index);
        return this;
    },

    // Subscribe to selection changes
    onSelect(callback) {
        window.onSpanSelect = callback;
        return this;
    },

    // Force redraw
    redraw() {
        BeamRenderer.draw();
        return this;
    },

    // Reset view (zoom/pan)
    resetView() {
        BeamState.zoom = 1.0;
        BeamState.panX = 0;
        BeamState.panY = 0;
        BeamRenderer.draw();
        return this;
    },

    // Expose state for external access
    State: BeamState,
    Renderer: BeamRenderer,
    Physics: BeamPhysics,
    Events: BeamEvents
};

// Export for module usage
if (typeof module !== 'undefined' && module.exports) {
    module.exports = BeamCanvas;
}
