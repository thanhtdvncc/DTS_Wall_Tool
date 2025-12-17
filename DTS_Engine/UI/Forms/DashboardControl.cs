using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace DTS_Engine.UI.Forms
{
    /// <summary>
    /// DashboardControl - WebView2 control for floating toolbar
    /// Handles HTML rendering and window dragging
    /// </summary>
    public class DashboardControl : UserControl
    {
        private WebView2 _webView;

        // --- WINDOWS API FOR WINDOW DRAGGING ---
        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HT_CAPTION = 0x2;

        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
        // -------------------------------------------

        public DashboardControl()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this._webView = new WebView2();
            this.SuspendLayout();

            // Configure WebView2 to fill control
            this._webView.CreationProperties = null;
            this._webView.DefaultBackgroundColor = Color.FromArgb(43, 43, 43); // Match HTML background
            this._webView.Dock = DockStyle.Fill;
            this._webView.Name = "_webView";
            this._webView.TabIndex = 0;
            this._webView.ZoomFactor = 1.0D;

            this.Controls.Add(this._webView);
            this.Name = "DashboardControl";
            this.Size = new Size(400, 45);
            this.ResumeLayout(false);

            InitializeAsync();
        }

        private async void InitializeAsync()
        {
            try
            {
                // 1. Create separate cache folder
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DTS_Dashboard_Cache");
                Directory.CreateDirectory(userDataFolder);

                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await _webView.EnsureCoreWebView2Async(env);

                // 2. Configure WebView settings
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;

                // 3. Register message handler
                _webView.WebMessageReceived += WebView_MessageReceived;

                // 4. Load HTML from embedded resource
                LoadHtml();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi khởi tạo Dashboard: " + ex.Message);
            }
        }

        private void LoadHtml()
        {
            var assembly = Assembly.GetExecutingAssembly();
            // Find file ending with Dashboard.html
            string resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(x => x.EndsWith("Dashboard.html"));

            if (!string.IsNullOrEmpty(resourceName))
            {
                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                using (StreamReader reader = new StreamReader(stream))
                {
                    _webView.NavigateToString(reader.ReadToEnd());
                }
            }
            else
            {
                _webView.NavigateToString("<h3 style='color:red;'>⚠️ Dashboard.html not found!</h3>");
            }
        }

        private void WebView_MessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string cmd = e.TryGetWebMessageAsString();

            // === HANDLE WINDOW DRAGGING ===
            if (cmd == "DRAG_WINDOW")
            {
                Form parentForm = this.FindForm();
                if (parentForm != null)
                {
                    // Trick Windows into thinking user is dragging title bar
                    ReleaseCapture();
                    SendMessage(parentForm.Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
                }
                return;
            }

            // === HANDLE AUTOCAD COMMANDS ===
            try
            {
                // Focus back to drawing view before executing command
                Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();

                // Send command to AutoCAD command line
                Application.DocumentManager.MdiActiveDocument?.SendStringToExecute(cmd + " ", true, false, false);
            }
            catch
            {
                // Ignore errors if no document is open
            }
        }
    }
}
