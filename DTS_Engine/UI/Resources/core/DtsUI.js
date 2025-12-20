/**
 * DtsUI.js - UI Utilities
 * Toast notifications, modal helpers, dropdown utils.
 * Uses canvas-based toasts instead of WebView2 alerts.
 */
(function (global) {
    'use strict';

    const DtsUI = {
        // ===== TOAST CONTAINER =====
        _toastContainer: null,

        /**
         * Initialize UI components
         */
        init() {
            this._createToastContainer();
        },

        /**
         * Create toast container element
         */
        _createToastContainer() {
            if (this._toastContainer) return this._toastContainer;

            const container = document.createElement('div');
            container.id = 'dts-toast-container';
            container.style.cssText = `
                position: fixed;
                bottom: 20px;
                left: 50%;
                transform: translateX(-50%);
                z-index: 9999;
                display: flex;
                flex-direction: column;
                align-items: center;
                gap: 8px;
                pointer-events: none;
            `;
            document.body.appendChild(container);
            this._toastContainer = container;
            return container;
        },

        /**
         * Show toast notification (canvas-based, no WebView2 alert!)
         * @param {string} message - Message to display
         * @param {string} type - 'success' | 'error' | 'warning' | 'info'
         * @param {number} duration - Duration in ms (default 2500)
         */
        showToast(message, type = 'info', duration = 2500) {
            const container = this._toastContainer || this._createToastContainer();

            const colors = {
                success: { bg: '#10b981', icon: '✓' },
                error: { bg: '#ef4444', icon: '✗' },
                warning: { bg: '#f59e0b', icon: '⚠' },
                info: { bg: '#3b82f6', icon: 'ℹ' }
            };
            const style = colors[type] || colors.info;

            const toast = document.createElement('div');
            toast.style.cssText = `
                background: ${style.bg};
                color: white;
                padding: 8px 16px;
                border-radius: 6px;
                font-size: 13px;
                font-weight: 500;
                box-shadow: 0 4px 12px rgba(0,0,0,0.15);
                opacity: 0;
                transition: opacity 0.2s, transform 0.2s;
                transform: translateY(10px);
            `;
            toast.textContent = `${style.icon} ${message}`;
            container.appendChild(toast);

            // Animate in
            requestAnimationFrame(() => {
                toast.style.opacity = '1';
                toast.style.transform = 'translateY(0)';
            });

            // Remove after duration
            setTimeout(() => {
                toast.style.opacity = '0';
                toast.style.transform = 'translateY(-10px)';
                setTimeout(() => toast.remove(), 200);
            }, duration);
        },

        /**
         * Populate a dropdown element
         * @param {HTMLSelectElement} select - Select element
         * @param {Array} options - [{value, text, disabled?, selected?}]
         */
        populateDropdown(select, options) {
            if (!select) return;
            select.innerHTML = '';
            options.forEach(opt => {
                const el = document.createElement('option');
                el.value = opt.value;
                el.textContent = opt.text;
                if (opt.disabled) el.disabled = true;
                if (opt.selected) el.selected = true;
                select.appendChild(el);
            });
        },

        /**
         * Show/hide element by ID
         */
        toggle(elementId, show) {
            const el = document.getElementById(elementId);
            if (el) {
                el.classList.toggle('hidden', !show);
            }
        }
    };

    // Export to global namespace
    global.Dts = global.Dts || {};
    global.Dts.UI = DtsUI;

})(window);
