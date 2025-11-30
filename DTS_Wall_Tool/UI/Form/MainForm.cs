using Autodesk.AutoCAD.DatabaseServices;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace DTS_Wall_Tool.UI.Forms
{
    /// <summary>
    /// Form chính của ứng dụng DTS Wall Tool
    /// Tuân thủ ISO 25010: Usability, Operability, User Interface Aesthetics
    /// </summary>
    public partial class MainForm : Form
    {
        #region Fields

        private TabControl _mainTabControl;
        private WallLineGenTab _wallLineGenTab;
        private LoadAssignmentTab _loadAssignmentTab;
        private AutoLoadTab _autoLoadTab;
        private StatusStrip _statusStrip;
        private ToolStripStatusLabel _statusLabel;
        private ToolStripProgressBar _progressBar;

        #endregion

        #region Constructor

        public MainForm()
        {
            InitializeComponent();
            InitializeUI();
            LoadSettings();
        }

        #endregion

        #region Initialization

        private void InitializeComponent()
        {
            this.SuspendLayout();

            // Form Properties
            this.Text = "SAP2000 Wall Tool (by thanhtdvncc)";
            this.Size = new Size(480, 580);
            this.MinimumSize = new Size(460, 500);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MaximizeBox = false;
            this.Font = new Font("Segoe UI", 9F);
            this.Icon = SystemIcons.Application;

            // Cho phép resize
            this.AutoScaleMode = AutoScaleMode.Dpi;

            this.ResumeLayout(false);
        }

        private void InitializeUI()
        {
            // Main Tab Control
            _mainTabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Padding = new Point(12, 6)
            };

            // Tab 1: Wall Line Gen
            var tabWallLineGen = new TabPage("Wall Line Gen");
            _wallLineGenTab = new WallLineGenTab { Dock = DockStyle.Fill };
            tabWallLineGen.Controls.Add(_wallLineGenTab);

            // Tab 2: Load Assignment
            var tabLoadAssignment = new TabPage("Load Assignment");
            _loadAssignmentTab = new LoadAssignmentTab { Dock = DockStyle.Fill };
            tabLoadAssignment.Controls.Add(_loadAssignmentTab);

            // Tab 3: Auto Load
            var tabAutoLoad = new TabPage("Auto Load");
            _autoLoadTab = new AutoLoadTab { Dock = DockStyle.Fill };
            tabAutoLoad.Controls.Add(_autoLoadTab);

            _mainTabControl.TabPages.Add(tabWallLineGen);
            _mainTabControl.TabPages.Add(tabLoadAssignment);
            _mainTabControl.TabPages.Add(tabAutoLoad);

            // Status Strip
            _statusStrip = new StatusStrip();
            _statusLabel = new ToolStripStatusLabel
            {
                Text = "Ready",
                Spring = true,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _progressBar = new ToolStripProgressBar
            {
                Visible = false,
                Width = 150
            };
            _statusStrip.Items.AddRange(new ToolStripItem[] { _statusLabel, _progressBar });

            // Main Panel để chứa TabControl (với padding)
            var mainPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(4)
            };
            mainPanel.Controls.Add(_mainTabControl);

            // Add controls to form
            this.Controls.Add(mainPanel);
            this.Controls.Add(_statusStrip);

            // Events
            this.FormClosing += MainForm_FormClosing;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Cập nhật status bar
        /// </summary>
        public void SetStatus(string message, bool showProgress = false)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => SetStatus(message, showProgress)));
                return;
            }

            _statusLabel.Text = message;
            _progressBar.Visible = showProgress;
        }

        /// <summary>
        /// Cập nhật tiến độ
        /// </summary>
        public void SetProgress(int percent)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => SetProgress(percent)));
                return;
            }

            _progressBar.Value = Math.Min(100, Math.Max(0, percent));
        }

        #endregion

        #region Settings

        private void LoadSettings()
        {
            try
            {
                // Load settings từ file hoặc registry
                _wallLineGenTab.LoadSettings();
                _loadAssignmentTab.LoadSettings();
                _autoLoadTab.LoadSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Load settings error: {ex.Message}");
            }
        }

        private void SaveSettings()
        {
            try
            {
                _wallLineGenTab.SaveSettings();
                _loadAssignmentTab.SaveSettings();
                _autoLoadTab.SaveSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Save settings error: {ex.Message}");
            }
        }

        #endregion

        #region Event Handlers

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveSettings();
        }

        #endregion
    }
}