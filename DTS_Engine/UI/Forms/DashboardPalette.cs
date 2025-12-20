using System;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace DTS_Engine.UI.Forms
{
    /// <summary>
    /// DashboardPalette - Manages floating borderless HTML toolbar
    /// Syncs with AutoCAD window (move/hide/show together)
    /// </summary>
    public static class DashboardPalette
    {
        private static Form _dashboardForm;
        private static Timer _syncTimer;
        private static Rectangle _lastCadRect;
        private static bool _lastCadMinimized;
        private static bool _userDragged = false;  // Track if user has manually moved the dashboard
        private static Point _userPosition = Point.Empty;  // Remember user's custom position
        private static bool _isUpdatingPosition = false;  // Flag to prevent LocationChanged during programmatic update

        // Offset from AutoCAD window top-right corner
        private const int OFFSET_RIGHT = 60;
        private const int OFFSET_TOP = 120;

        // Windows API for window position
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
            public Rectangle ToRectangle() => new Rectangle(Left, Top, Right - Left, Bottom - Top);
        }

        /// <summary>
        /// Show the floating Dashboard toolbar
        /// </summary>
        public static void ShowPalette()
        {
            if (_dashboardForm == null || _dashboardForm.IsDisposed)
            {
                CreateDashboardForm();
            }

            UpdatePosition();
            _dashboardForm.Show();

            // Start sync timer
            StartSyncTimer();
        }

        private static void CreateDashboardForm()
        {
            _dashboardForm = new Form();

            // Borderless window
            _dashboardForm.FormBorderStyle = FormBorderStyle.None;
            _dashboardForm.ShowInTaskbar = false;
            _dashboardForm.StartPosition = FormStartPosition.Manual;
            _dashboardForm.TopMost = false;  // Normal window - no interference with AutoCAD
            _dashboardForm.AutoScaleMode = AutoScaleMode.None; // Prevent DPI scaling
            _dashboardForm.MinimumSize = Size.Empty; // Allow small size

            // Size: drag(20) + 6 buttons(120) + separator(10) + 7 buttons(140) = 290px width, 20px height
            int dWidth = 290;
            int dHeight = 20;
            _dashboardForm.Size = new Size(dWidth, dHeight);

            // FORCE SHAPE: Use Region to clip any OS-enforced minimum HEIGHT
            _dashboardForm.Region = new Region(new Rectangle(0, 0, dWidth, dHeight));

            // Transparent background
            _dashboardForm.BackColor = Color.White;
            _dashboardForm.TransparencyKey = Color.Magenta; // Not used but ready

            // Embed WebView2 control
            var control = new DashboardControl();
            control.Dock = DockStyle.Fill;
            _dashboardForm.Controls.Add(control);

            // Save position when user drags the form
            _dashboardForm.LocationChanged += (s, e) =>
            {
                // Only save if form is visible and not during programmatic positioning
                if (_dashboardForm.Visible && !_isUpdatingPosition)
                {
                    _userDragged = true;
                    _userPosition = _dashboardForm.Location;
                }
            };

            // Set AutoCAD as owner - Dashboard floats above CAD but goes behind other apps
            try
            {
                var cadOwner = new NativeWindow();
                cadOwner.AssignHandle(App.MainWindow.Handle);
                _dashboardForm.Show(cadOwner);
            }
            catch
            {
                // Fallback if owner assignment fails
            }
        }

        private static void StartSyncTimer()
        {
            if (_syncTimer != null) return;

            _syncTimer = new Timer();
            _syncTimer.Interval = 100; // Check every 100ms
            _syncTimer.Tick += SyncTimer_Tick;
            _syncTimer.Start();

            // Init last position
            _lastCadRect = GetCadWindowRect();
            _lastCadMinimized = IsCadMinimized();
        }

        private static void SyncTimer_Tick(object sender, EventArgs e)
        {
            if (_dashboardForm == null || _dashboardForm.IsDisposed)
            {
                StopSyncTimer();
                return;
            }

            try
            {
                bool cadMinimized = IsCadMinimized();

                // ONLY track minimize/restore - nothing else
                if (cadMinimized != _lastCadMinimized)
                {
                    if (cadMinimized)
                    {
                        _dashboardForm.Hide();
                    }
                    else
                    {
                        _dashboardForm.Show();
                    }
                    _lastCadMinimized = cadMinimized;
                }
                // No TopMost toggling, no position tracking, no focus detection
            }
            catch
            {
                // AutoCAD may not be available
            }
        }

        private static Rectangle GetCadWindowRect()
        {
            try
            {
                IntPtr handle = App.MainWindow.Handle;
                if (GetWindowRect(handle, out RECT rect))
                {
                    return rect.ToRectangle();
                }
            }
            catch { }
            return Rectangle.Empty;
        }

        private static bool IsCadMinimized()
        {
            try
            {
                return IsIconic(App.MainWindow.Handle);
            }
            catch { return false; }
        }

        private static bool IsCadVisible()
        {
            try
            {
                return IsWindowVisible(App.MainWindow.Handle);
            }
            catch { return false; }
        }

        /// <summary>
        /// Check if AutoCAD or its child windows (including our Dashboard) have focus
        /// </summary>
        private static bool IsCadFocused()
        {
            try
            {
                IntPtr foreground = GetForegroundWindow();
                IntPtr cadHandle = App.MainWindow.Handle;

                // AutoCAD itself is focused
                if (foreground == cadHandle) return true;

                // Our Dashboard is focused (still count as CAD focused)
                if (_dashboardForm != null && !_dashboardForm.IsDisposed && foreground == _dashboardForm.Handle)
                    return true;

                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Calculate the auto-position for Dashboard based on AutoCAD window
        /// </summary>
        private static Point CalculateAutoPosition(Rectangle cadRect)
        {
            if (_dashboardForm == null || cadRect.IsEmpty)
                return new Point(100, 100);

            int x = cadRect.Right - _dashboardForm.Width - OFFSET_RIGHT;
            int y = cadRect.Top + OFFSET_TOP;

            if (x < 0) x = 10;
            if (y < 0) y = 10;

            return new Point(x, y);
        }

        private static void UpdatePosition()
        {
            if (_dashboardForm == null || _dashboardForm.IsDisposed) return;

            // If user has manually dragged, use their position
            if (_userDragged && _userPosition != Point.Empty)
            {
                _dashboardForm.Location = _userPosition;
                return;
            }

            Rectangle cadRect = GetCadWindowRect();
            if (cadRect.IsEmpty)
            {
                _dashboardForm.Location = new Point(100, 100);
                return;
            }

            // Position at top-right of AutoCAD window
            int x = cadRect.Right - _dashboardForm.Width - OFFSET_RIGHT;
            int y = cadRect.Top + OFFSET_TOP;

            // Bounds check
            if (x < 0) x = 10;
            if (y < 0) y = 10;

            // Use flag to prevent LocationChanged from triggering during programmatic update
            _isUpdatingPosition = true;
            _dashboardForm.Location = new Point(x, y);
            _isUpdatingPosition = false;
        }

        /// <summary>
        /// Called by DashboardControl when user finishes dragging
        /// </summary>
        public static void OnUserDragged(Point newPosition)
        {
            _userDragged = true;
            _userPosition = newPosition;
        }

        /// <summary>
        /// Reset to auto-positioning (follow AutoCAD)
        /// </summary>
        public static void ResetPosition()
        {
            _userDragged = false;
            _userPosition = Point.Empty;
            UpdatePosition();
        }

        private static void StopSyncTimer()
        {
            if (_syncTimer != null)
            {
                _syncTimer.Stop();
                _syncTimer.Dispose();
                _syncTimer = null;
            }
        }

        /// <summary>
        /// Hide the Dashboard
        /// </summary>
        public static void HidePalette()
        {
            StopSyncTimer();
            if (_dashboardForm != null && !_dashboardForm.IsDisposed)
            {
                _dashboardForm.Hide();
            }
        }

        /// <summary>
        /// Toggle Dashboard visibility
        /// </summary>
        public static void TogglePalette()
        {
            if (_dashboardForm == null || _dashboardForm.IsDisposed)
            {
                ShowPalette();
            }
            else
            {
                if (_dashboardForm.Visible)
                {
                    HidePalette();
                }
                else
                {
                    ShowPalette();
                }
            }
        }

        /// <summary>
        /// Close and dispose Dashboard (for DLL unload)
        /// </summary>
        public static void ClosePalette()
        {
            StopSyncTimer();
            if (_dashboardForm != null)
            {
                _dashboardForm.Close();
                _dashboardForm.Dispose();
                _dashboardForm = null;
            }
        }
    }
}
