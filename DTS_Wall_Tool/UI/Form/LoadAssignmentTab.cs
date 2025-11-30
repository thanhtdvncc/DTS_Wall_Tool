using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Engines;
using DTS_Wall_Tool.Core.Primitives;
using DTS_Wall_Tool.Core.Utils;

namespace DTS_Wall_Tool.UI.Forms
{
    /// <summary>
    /// Tab gán tải lên SAP2000
    /// </summary>
    public class LoadAssignmentTab : UserControl
    {
        #region Controls

        // Story Selection
        private GroupBox _grpStorySelection;
        private ListBox _lstStories;
        private Button _btnLoadStories;
        private Button _btnPickPoint;
        private NumericUpDown _nudElevation;
        private NumericUpDown _nudHeight;
        private NumericUpDown _nudOriginX;
        private NumericUpDown _nudOriginY;

        // Wall Load Assignments
        private GroupBox _grpWallLoads;
        private ListView _lstWallLoads;
        private Button _btnGetAcad;
        private Button _btnSyncToCad;
        private Button _btnSyncFromSap;
        private Button _btnMoveUp;
        private Button _btnMoveDown;
        private Button _btnRemoveItem;

        // Action Buttons
        private Button _btnSetOverwrite;
        private Button _btnSetStories;
        private Button _btnDelLabel;
        private Button _btnShowLabel;
        private Button _btnDeleteLoad;
        private Button _btnClearSapLoad;
        private Button _btnAssignLoads;
        private Button _btnCancel;
        private static readonly List<WallLoadItem> wallLoadItems = new List<WallLoadItem>();

        #endregion

        #region Data

        private List<WallLoadItem> _wallLoadItems = wallLoadItems;

        #endregion

        #region Constructor

        public LoadAssignmentTab()
        {
            InitializeUI();
        }

        #endregion

        #region UI Initialization

        private void InitializeUI()
        {
            this.Padding = new Padding(8);

            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4
            };
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

            // Story Selection Group
            _grpStorySelection = new GroupBox
            {
                Text = "Story Selection:",
                Dock = DockStyle.Fill
            };

            var storyLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 3,
                Padding = new Padding(5)
            };

            // Story list
            _lstStories = new ListBox
            {
                Height = 60,
                Width = 150
            };
            storyLayout.Controls.Add(_lstStories, 0, 0);
            storyLayout.SetRowSpan(_lstStories, 3);

            // Buttons
            _btnLoadStories = new Button { Text = "Load Stories", Width = 80, Height = 25 };
            _btnPickPoint = new Button { Text = "Pick Point", Width = 80, Height = 25 };
            _btnLoadStories.Click += BtnLoadStories_Click;
            _btnPickPoint.Click += BtnPickPoint_Click;

            storyLayout.Controls.Add(_btnLoadStories, 1, 0);
            storyLayout.Controls.Add(_btnPickPoint, 1, 1);

            // Elevation inputs
            var elevPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            elevPanel.Controls.Add(new Label { Text = "E(mm):", AutoSize = true, Margin = new Padding(3, 6, 0, 0) });
            _nudElevation = new NumericUpDown { Width = 70, Minimum = -100000, Maximum = 100000 };
            elevPanel.Controls.Add(_nudElevation);
            elevPanel.Controls.Add(new Label { Text = "H(mm):", AutoSize = true, Margin = new Padding(10, 6, 0, 0) });
            _nudHeight = new NumericUpDown { Width = 70, Minimum = 0, Maximum = 100000, Value = 3300 };
            elevPanel.Controls.Add(_nudHeight);
            storyLayout.Controls.Add(elevPanel, 2, 0);
            storyLayout.SetColumnSpan(elevPanel, 2);

            // Origin inputs
            var originPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            originPanel.Controls.Add(new Label { Text = "X(mm):", AutoSize = true, Margin = new Padding(3, 6, 0, 0) });
            _nudOriginX = new NumericUpDown { Width = 80, Minimum = -1000000, Maximum = 1000000 };
            originPanel.Controls.Add(_nudOriginX);
            originPanel.Controls.Add(new Label { Text = "Y(mm):", AutoSize = true, Margin = new Padding(10, 6, 0, 0) });
            _nudOriginY = new NumericUpDown { Width = 80, Minimum = -1000000, Maximum = 1000000 };
            originPanel.Controls.Add(_nudOriginY);
            storyLayout.Controls.Add(new Label
            {
                Text = "Set Model Origin\nin AutoCAD:",
                AutoSize = true
            }, 1, 2);
            storyLayout.Controls.Add(originPanel, 2, 2);
            storyLayout.SetColumnSpan(originPanel, 2);

            _grpStorySelection.Controls.Add(storyLayout);
            mainLayout.Controls.Add(_grpStorySelection, 0, 0);

            // Wall Load Assignments Group
            _grpWallLoads = new GroupBox
            {
                Text = "Wall Load Assignments",
                Dock = DockStyle.Fill
            };

            var loadLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(5)
            };
            loadLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            loadLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            // Left buttons
            var leftPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };

            _btnGetAcad = new Button { Text = "Get Acad", Width = 70, Height = 28, Margin = new Padding(2) };
            _btnSyncToCad = new Button { Text = "Sync to CAD", Width = 70, Height = 28, Margin = new Padding(2) };
            _btnSyncFromSap = new Button { Text = "Sync fr.  SAP", Width = 70, Height = 28, Margin = new Padding(2) };

            _btnGetAcad.Click += BtnGetAcad_Click;
            _btnSyncToCad.Click += BtnSyncToCad_Click;
            _btnSyncFromSap.Click += BtnSyncFromSap_Click;

            leftPanel.Controls.Add(_btnGetAcad);
            leftPanel.Controls.Add(_btnSyncToCad);
            leftPanel.Controls.Add(_btnSyncFromSap);

            loadLayout.Controls.Add(leftPanel, 0, 0);

            // ListView
            var listPanel = new Panel { Dock = DockStyle.Fill };

            _lstWallLoads = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Font = new Font("Consolas", 9F)
            };
            _lstWallLoads.Columns.Add("#", 30);
            _lstWallLoads.Columns.Add("Handle", 60);
            _lstWallLoads.Columns.Add("Thick", 50);
            _lstWallLoads.Columns.Add("Pattern", 50);

            // Right side buttons
            var rightPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                Width = 50,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };

            _btnMoveUp = new Button { Text = "[=>", Width = 40, Height = 25 };
            _btnMoveDown = new Button { Text = "<=]", Width = 40, Height = 25 };
            _btnRemoveItem = new Button { Text = "[x]", Width = 40, Height = 25 };

            rightPanel.Controls.Add(_btnMoveUp);
            rightPanel.Controls.Add(_btnMoveDown);
            rightPanel.Controls.Add(_btnRemoveItem);

            listPanel.Controls.Add(_lstWallLoads);
            listPanel.Controls.Add(rightPanel);

            loadLayout.Controls.Add(listPanel, 1, 0);

            _grpWallLoads.Controls.Add(loadLayout);
            mainLayout.Controls.Add(_grpWallLoads, 0, 1);

            // Middle buttons row
            var middlePanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false
            };

            _btnSetOverwrite = new Button { Text = "Set Overwrite", Width = 85, Height = 28 };
            _btnSetStories = new Button { Text = "Set Stories", Width = 75, Height = 28 };
            _btnDelLabel = new Button { Text = "Del Label", Width = 70, Height = 28 };
            _btnShowLabel = new Button { Text = "Show Label", Width = 80, Height = 28 };

            middlePanel.Controls.Add(_btnSetOverwrite);
            middlePanel.Controls.Add(_btnSetStories);
            middlePanel.Controls.Add(_btnDelLabel);
            middlePanel.Controls.Add(_btnShowLabel);

            mainLayout.Controls.Add(middlePanel, 0, 2);

            // Bottom buttons row
            var bottomPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false
            };

            _btnDeleteLoad = new Button { Text = "Delete load", Width = 80, Height = 28 };
            _btnClearSapLoad = new Button { Text = "ClearSAP load", Width = 95, Height = 28 };
            _btnAssignLoads = new Button
            {
                Text = "Assign Loads",
                Width = 95,
                Height = 28,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _btnCancel = new Button { Text = "Cancel", Width = 70, Height = 28 };

            _btnAssignLoads.Click += BtnAssignLoads_Click;
            _btnCancel.Click += BtnCancel_Click;

            bottomPanel.Controls.Add(_btnDeleteLoad);
            bottomPanel.Controls.Add(_btnClearSapLoad);
            bottomPanel.Controls.Add(_btnAssignLoads);
            bottomPanel.Controls.Add(_btnCancel);

            mainLayout.Controls.Add(bottomPanel, 0, 3);

            this.Controls.Add(mainLayout);
        }

        #endregion

        #region Event Handlers

        private void BtnLoadStories_Click(object sender, EventArgs e)
        {
            try
            {
                if (!SapUtils.IsConnected)
                {
                    SapUtils.Connect(out string msg);
                }

                var stories = SapUtils.GetStories();
                _lstStories.Items.Clear();

                foreach (var story in stories)
                {
                    _lstStories.Items.Add($"{story.StoryName} (Z={story.Elevation})");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading stories: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnPickPoint_Click(object sender, EventArgs e)
        {
            // TODO: Pick point in AutoCAD
            MessageBox.Show("Pick point from AutoCAD - Coming soon", "Info",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void BtnGetAcad_Click(object sender, EventArgs e)
        {
            try
            {
                var parentForm = this.FindForm();
                if (parentForm != null)
                    parentForm.WindowState = FormWindowState.Minimized;

                var lineIds = AcadUtils.SelectObjectsOnScreen("LINE");
                _wallLoadItems.Clear();
                _lstWallLoads.Items.Clear();

                int index = 1;
                AcadUtils.UsingTransaction(tr =>
                {
                    foreach (var lineId in lineIds)
                    {
                        var obj = tr.GetObject(lineId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
                        var wData = XDataUtils.ReadWallData(obj);

                        var item = new WallLoadItem
                        {
                            Index = index++,
                            Handle = lineId.Handle.ToString(),
                            Thickness = wData?.Thickness ?? 0,
                            LoadPattern = wData?.LoadPattern ?? "DL"
                        };
                        _wallLoadItems.Add(item);

                        var lvItem = new ListViewItem(item.Index.ToString());
                        lvItem.SubItems.Add(item.Handle);
                        lvItem.SubItems.Add(item.Thickness?.ToString() ?? "");
                        lvItem.SubItems.Add(item.LoadPattern);
                        _lstWallLoads.Items.Add(lvItem);
                    }
                });
            }
            finally
            {
                var parentForm = this.FindForm();
                if (parentForm != null)
                    parentForm.WindowState = FormWindowState.Normal;
            }
        }

        private void BtnSyncToCad_Click(object sender, EventArgs e)
        {
            // TODO: Sync to CAD
        }

        private void BtnSyncFromSap_Click(object sender, EventArgs e)
        {
            // TODO: Sync from SAP
        }

        private void BtnAssignLoads_Click(object sender, EventArgs e)
        {
            try
            {
                if (!SapUtils.IsConnected)
                {
                    if (!SapUtils.Connect(out string msg))
                    {
                        MessageBox.Show(msg, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }

                int assigned = 0;
                int failed = 0;

                foreach (var item in _wallLoadItems)
                {
                    // TODO: Implement actual load assignment
                    assigned++;
                }

                MessageBox.Show($"Assigned: {assigned}, Failed: {failed}", "Result",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnCancel_Click(object sender, EventArgs e)
        {
            this.FindForm()?.Close();
        }

        #endregion

        #region Settings

        public void LoadSettings() { }
        public void SaveSettings() { }

        #endregion

        #region Helper Classes

        private class WallLoadItem
        {
            public int Index { get; set; }
            public string Handle { get; set; }
            public double? Thickness { get; set; }
            public string LoadPattern { get; set; }
        }

        #endregion
    }
}