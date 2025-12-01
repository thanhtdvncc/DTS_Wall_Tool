using DTS_Wall_Tool.Core.Engines;
using DTS_Wall_Tool.Core.Primitives;
using DTS_Wall_Tool.Core.Utils;
using DTS_Wall_Tool.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DTS_Wall_Tool.UI.Forms
{
    /// <summary>
    /// Tab xử lý Wall Line Generation
    /// </summary>
    public class WallLineGenTab : UserControl
    {
        #region Controls

        // Input Controls
        private NumericUpDown _nudWallThickness;
        private TextBox _txtLayerDetect;
        private NumericUpDown _nudAngleTolerance;
        private TextBox _txtAxesLine;
        private Button _btnPickAxes;
        private Button _btnClearAxes;
        private TextBox _txtDoorWidths;
        private TextBox _txtColumnWidths;
        private NumericUpDown _nudExtendCoeff;
        private CheckBox _chkAutoExtend;
        private NumericUpDown _nudWallThkTolerance;
        private CheckBox _chkCreateIntersection;
        private NumericUpDown _nudAutoJoinGap;
        private NumericUpDown _nudAxisSnap;
        private CheckBox _chkBreakAtGrid;
        private CheckBox _chkExtendOnGrid;

        // Action Buttons
        private Button _btnDefaults;
        private Button _btnRun;
        private Button _btnCancel;

        #endregion

        #region Data

        private List<AxisLine> _selectedAxes = new List<AxisLine>();

        #endregion

        #region Constructor

        public WallLineGenTab()
        {
            InitializeUI();
        }

        #endregion

        #region UI Initialization

        private void InitializeUI()
        {
            this.Padding = new Padding(8);
            this.AutoScroll = true;

            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 12,
                AutoSize = true
            };

            // Column styles
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));

            int row = 0;

            // Row 0: Wall thickness
            mainLayout.Controls.Add(CreateLabel("Wall thickness\n(mm):"), 0, row);
            _nudWallThickness = CreateNumericUpDown(200, 50, 1000, 0);
            mainLayout.Controls.Add(_nudWallThickness, 1, row);

            // Row 1: Layer to detect
            row++;
            mainLayout.Controls.Add(CreateLabel("Layer to detect? :"), 0, row);
            _txtLayerDetect = new TextBox { Text = "C-WALL", Dock = DockStyle.Fill };
            mainLayout.Controls.Add(_txtLayerDetect, 1, row);
            mainLayout.SetColumnSpan(_txtLayerDetect, 3);

            // Row 2: Angle tolerance
            row++;
            mainLayout.Controls.Add(CreateLabel("Angle tolerance\n(degree):"), 0, row);
            _nudAngleTolerance = CreateNumericUpDown(5, 0, 45, 1);
            mainLayout.Controls.Add(_nudAngleTolerance, 1, row);

            var axesPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            _btnPickAxes = new Button { Text = "Pick", Width = 50 };
            _btnClearAxes = new Button { Text = "[ x ]", Width = 40 };
            _btnPickAxes.Click += BtnPickAxes_Click;
            _btnClearAxes.Click += BtnClearAxes_Click;
            axesPanel.Controls.Add(_btnPickAxes);
            axesPanel.Controls.Add(_btnClearAxes);
            mainLayout.Controls.Add(axesPanel, 3, row);

            // Row 3: Axes Line
            row++;
            mainLayout.Controls.Add(CreateLabel("Axes Line:"), 0, row);
            _txtAxesLine = new TextBox
            {
                Text = "No axes selected\n(optional)",
                Dock = DockStyle.Fill,
                ReadOnly = true,
                ForeColor = Color.Gray,
                Multiline = true,
                Height = 40
            };
            mainLayout.Controls.Add(_txtAxesLine, 1, row);
            mainLayout.SetColumnSpan(_txtAxesLine, 2);

            var axesPanel2 = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            var btnPickAxes2 = new Button { Text = "Pick", Width = 50 };
            var btnClearAxes2 = new Button { Text = "[ x ]", Width = 40 };
            btnPickAxes2.Click += BtnPickAxes_Click;
            btnClearAxes2.Click += BtnClearAxes_Click;
            axesPanel2.Controls.Add(btnPickAxes2);
            axesPanel2.Controls.Add(btnClearAxes2);
            mainLayout.Controls.Add(axesPanel2, 3, row);

            // Row 4: Door/Windows widths
            row++;
            mainLayout.Controls.Add(CreateLabel("Door, Windows\nwidths (mm):"), 0, row);
            _txtDoorWidths = new TextBox
            {
                Text = "2000,700,750,900,1200,1000,1300,3000",
                Dock = DockStyle.Fill
            };
            mainLayout.Controls.Add(_txtDoorWidths, 1, row);
            mainLayout.SetColumnSpan(_txtDoorWidths, 3);

            // Row 5: Column widths
            row++;
            mainLayout.Controls.Add(CreateLabel("Columns widths\n(mm):"), 0, row);
            _txtColumnWidths = new TextBox { Text = "800,400", Dock = DockStyle.Fill };
            mainLayout.Controls.Add(_txtColumnWidths, 1, row);
            mainLayout.SetColumnSpan(_txtColumnWidths, 3);

            // Row 6: Extend coefficient + Auto extend checkbox
            row++;
            mainLayout.Controls.Add(CreateLabel("Extend coefficient\n(times):"), 0, row);
            _nudExtendCoeff = CreateNumericUpDown(2, 0, 10, 1);
            mainLayout.Controls.Add(_nudExtendCoeff, 1, row);
            _chkAutoExtend = new CheckBox
            {
                Text = "Automatically extend\nperpendicular walls",
                Checked = true,
                AutoSize = true
            };
            mainLayout.Controls.Add(_chkAutoExtend, 2, row);
            mainLayout.SetColumnSpan(_chkAutoExtend, 2);

            // Row 7: Wall thickness tolerance + Create intersection
            row++;
            mainLayout.Controls.Add(CreateLabel("Wall thk.\ntolerance (mm):"), 0, row);
            _nudWallThkTolerance = CreateNumericUpDown(5, 0, 50, 0);
            mainLayout.Controls.Add(_nudWallThkTolerance, 1, row);
            _chkCreateIntersection = new CheckBox
            {
                Text = "Create intersection\nat intersection",
                Checked = true,
                AutoSize = true
            };
            mainLayout.Controls.Add(_chkCreateIntersection, 2, row);
            mainLayout.SetColumnSpan(_chkCreateIntersection, 2);

            // Row 8: Auto Join Gap / Axis Snap
            row++;
            mainLayout.Controls.Add(CreateLabel("Auto Join Gap /\nAxis Snap (mm):"), 0, row);
            _nudAutoJoinGap = CreateNumericUpDown(400, 0, 2000, 0);
            mainLayout.Controls.Add(_nudAutoJoinGap, 1, row);
            _nudAxisSnap = CreateNumericUpDown(500, 0, 2000, 0);
            mainLayout.Controls.Add(_nudAxisSnap, 3, row);

            // Row 9: Checkboxes
            row++;
            _chkBreakAtGrid = new CheckBox
            {
                Text = "Break lines at grid intersections",
                Checked = true,
                AutoSize = true
            };
            mainLayout.Controls.Add(_chkBreakAtGrid, 0, row);
            mainLayout.SetColumnSpan(_chkBreakAtGrid, 2);

            _chkExtendOnGrid = new CheckBox
            {
                Text = "Also extend line on grid",
                Checked = true,
                AutoSize = true
            };
            mainLayout.Controls.Add(_chkExtendOnGrid, 2, row);
            mainLayout.SetColumnSpan(_chkExtendOnGrid, 2);

            // Row 10: Separator
            row++;
            var separator = new Panel
            {
                Height = 2,
                Dock = DockStyle.Top,
                BackColor = Color.LightGray,
                Margin = new Padding(0, 10, 0, 10)
            };
            mainLayout.Controls.Add(separator, 0, row);
            mainLayout.SetColumnSpan(separator, 4);

            // Row 11: Buttons
            row++;
            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };

            _btnCancel = new Button { Text = "Cancel", Width = 75, Height = 28 };
            _btnRun = new Button
            {
                Text = "Run",
                Width = 85,
                Height = 28,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _btnDefaults = new Button { Text = "Defaults", Width = 75, Height = 28 };

            _btnCancel.Click += BtnCancel_Click;
            _btnRun.Click += BtnRun_Click;
            _btnDefaults.Click += BtnDefaults_Click;

            buttonPanel.Controls.Add(_btnCancel);
            buttonPanel.Controls.Add(_btnRun);
            buttonPanel.Controls.Add(_btnDefaults);

            mainLayout.Controls.Add(buttonPanel, 0, row);
            mainLayout.SetColumnSpan(buttonPanel, 4);

            this.Controls.Add(mainLayout);
        }

        #endregion

        #region Helper Methods

        private Label CreateLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(3, 6, 3, 3)
            };
        }

        private NumericUpDown CreateNumericUpDown(decimal value, decimal min, decimal max, int decimalPlaces)
        {
            return new NumericUpDown
            {
                Value = value,
                Minimum = min,
                Maximum = max,
                DecimalPlaces = decimalPlaces,
                Width = 80,
                Margin = new Padding(3)
            };
        }

        private List<double> ParseDoubleList(string text)
        {
            var list = new List<double>();
            if (string.IsNullOrEmpty(text)) return list;

            foreach (var s in text.Split(','))
            {
                if (double.TryParse(s.Trim(), out double d))
                    list.Add(d);
            }
            return list;
        }

        #endregion

        #region Event Handlers

        private void BtnPickAxes_Click(object sender, EventArgs e)
        {
            // Ẩn form, pick axes trong AutoCAD, hiện lại form
            var parentForm = this.FindForm();
            if (parentForm != null)
                parentForm.WindowState = FormWindowState.Minimized;

            try
            {
                var lineIds = AcadUtils.SelectObjectsOnScreen("LINE");
                _selectedAxes.Clear();

                AcadUtils.UsingTransaction(tr =>
                {
                    foreach (var lineId in lineIds)
                    {
                        var line = tr.GetObject(lineId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead)
                            as Autodesk.AutoCAD.DatabaseServices.Line;
                        if (line != null)
                        {
                            _selectedAxes.Add(new AxisLine(
                                new Point2D(line.StartPoint.X, line.StartPoint.Y),
                                new Point2D(line.EndPoint.X, line.EndPoint.Y),
                                lineId.Handle.ToString()
                            ));
                        }
                    }
                });

                _txtAxesLine.Text = _selectedAxes.Count > 0
                    ? $"{_selectedAxes.Count} axes selected"
                    : "No axes selected\n(optional)";
                _txtAxesLine.ForeColor = _selectedAxes.Count > 0 ? Color.Black : Color.Gray;
            }
            finally
            {
                if (parentForm != null)
                    parentForm.WindowState = FormWindowState.Normal;
            }
        }

        private void BtnClearAxes_Click(object sender, EventArgs e)
        {
            _selectedAxes.Clear();
            _txtAxesLine.Text = "No axes selected\n(optional)";
            _txtAxesLine.ForeColor = Color.Gray;
        }

        private void BtnDefaults_Click(object sender, EventArgs e)
        {
            _nudWallThickness.Value = 200;
            _txtLayerDetect.Text = "C-WALL";
            _nudAngleTolerance.Value = 5;
            _txtDoorWidths.Text = "2000,700,750,900,1200,1000,1300,3000";
            _txtColumnWidths.Text = "800,400";
            _nudExtendCoeff.Value = 2;
            _chkAutoExtend.Checked = true;
            _nudWallThkTolerance.Value = 5;
            _chkCreateIntersection.Checked = true;
            _nudAutoJoinGap.Value = 400;
            _nudAxisSnap.Value = 500;
            _chkBreakAtGrid.Checked = true;
            _chkExtendOnGrid.Checked = true;
            BtnClearAxes_Click(sender, e);
        }

        private void BtnRun_Click(object sender, EventArgs e)
        {
            try
            {
                var parentForm = this.FindForm();
                if (parentForm != null)
                    parentForm.WindowState = FormWindowState.Minimized;

                // Thu thập settings
                var processor = new WallSegmentProcessor
                {
                    WallThicknesses = new List<double> { (double)_nudWallThickness.Value },
                    DoorWidths = ParseDoubleList(_txtDoorWidths.Text),
                    ColumnWidths = ParseDoubleList(_txtColumnWidths.Text),
                    AngleTolerance = (double)_nudAngleTolerance.Value,
                    DistanceTolerance = (double)_nudWallThkTolerance.Value,
                    AxisSnapDistance = (double)_nudAxisSnap.Value,
                    AutoJoinGapDistance = (double)_nudAutoJoinGap.Value,
                    EnableAutoExtend = _chkAutoExtend.Checked,
                    BreakAtGridIntersections = _chkBreakAtGrid.Checked,
                    ExtendToGridIntersections = _chkExtendOnGrid.Checked
                };

                // Chọn lines
                string layer = _txtLayerDetect.Text.Trim();
                var lineIds = AcadUtils.SelectObjectsOnScreen("LINE");

                if (lineIds.Count == 0)
                {
                    MessageBox.Show("No lines selected!", "Warning",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Thu thập segments
                var segments = new List<WallSegment>();
                AcadUtils.UsingTransaction(tr =>
                {
                    foreach (var lineId in lineIds)
                    {
                        var line = tr.GetObject(lineId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead)
                            as Autodesk.AutoCAD.DatabaseServices.Line;
                        if (line == null) continue;

                        if (!string.IsNullOrEmpty(layer) && !line.Layer.Equals(layer, StringComparison.OrdinalIgnoreCase))
                            continue;

                        segments.Add(new WallSegment(
                            new Point2D(line.StartPoint.X, line.StartPoint.Y),
                            new Point2D(line.EndPoint.X, line.EndPoint.Y)
                        )
                        {
                            Handle = lineId.Handle.ToString(),
                            Layer = line.Layer
                        });
                    }
                });

                // Xử lý
                var centerlines = processor.Process(segments, _selectedAxes);

                // Vẽ kết quả
                AcadUtils.CreateLayer("dts_centerlines", 4);
                AcadUtils.ClearLayer("dts_centerlines");

                AcadUtils.UsingTransaction(tr =>
                {
                    foreach (var cl in centerlines)
                    {
                        AcadUtils.CreateLine(cl.AsSegment, "dts_centerlines", 4, tr);
                    }
                });

                MessageBox.Show(
                    $"Processing complete!\n\n" +
                    $"Input segments: {segments.Count}\n" +
                    $"Merged: {processor.MergedSegmentsCount}\n" +
                    $"Pairs detected: {processor.DetectedPairsCount}\n" +
                    $"Gaps recovered: {processor.RecoveredGapsCount}\n" +
                    $"Output centerlines: {centerlines.Count}",
                    "Success",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                var parentForm = this.FindForm();
                if (parentForm != null)
                    parentForm.WindowState = FormWindowState.Normal;
            }
        }

        private void BtnCancel_Click(object sender, EventArgs e)
        {
            this.FindForm()?.Close();
        }

        #endregion

        #region Settings

        public void LoadSettings()
        {
            // TODO: Load from settings file
        }

        public void SaveSettings()
        {
            // TODO: Save to settings file
        }

        #endregion

        private void InitializeComponent()
        {
            this.SuspendLayout();
            // 
            // WallLineGenTab
            // 
            this.Name = "WallLineGenTab";
            this.Size = new System.Drawing.Size(415, 515);
            this.ResumeLayout(false);

        }
    }
}