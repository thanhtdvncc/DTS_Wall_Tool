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
            _dashboardForm.TopMost = true;

            // Size: 9 buttons * 28px + gaps + padding = ~300px width, 30px height
            _dashboardForm.Size = new Size(300, 30);

            // Background (WebView handles actual transparency)
            _dashboardForm.BackColor = Color.White;

            // Embed WebView2 control
            var control = new DashboardControl();
            control.Dock = DockStyle.Fill;
            _dashboardForm.Controls.Add(control);
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
                Rectangle cadRect = GetCadWindowRect();
                bool cadMinimized = IsCadMinimized();
                bool cadVisible = IsCadVisible();

                // Check if CAD window moved or resized
                if (cadRect != _lastCadRect)
                {
                    UpdatePosition();
                    _lastCadRect = cadRect;
                }

                // Check if CAD minimized/restored
                if (cadMinimized != _lastCadMinimized)
                {
                    if (cadMinimized)
                    {
                        _dashboardForm.Hide();
                    }
                    else
                    {
                        _dashboardForm.Show();
                        UpdatePosition();
                    }
                    _lastCadMinimized = cadMinimized;
                }

                // Also hide if CAD is not visible
                if (!cadVisible && _dashboardForm.Visible)
                {
                    _dashboardForm.Hide();
                }
                else if (cadVisible && !cadMinimized && !_dashboardForm.Visible)
                {
                    _dashboardForm.Show();
                    UpdatePosition();
                }
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

        private static void UpdatePosition()
        {
            if (_dashboardForm == null || _dashboardForm.IsDisposed) return;

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

            _dashboardForm.Location = new Point(x, y);
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
