using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Autodesk.AutoCAD.DatabaseServices;
using DTS_Wall_Tool.Core.Engines;
using DTS_Wall_Tool.Core.Primitives;
using DTS_Wall_Tool.Core.Utils;
using DTS_Wall_Tool.Models;

namespace DTS_Wall_Tool.UI.Forms
{
	// Token: 0x0200000A RID: 10
	public class WallLineGenTab : UserControl
	{
		// Token: 0x06000041 RID: 65 RVA: 0x00004DF6 File Offset: 0x00002FF6
		public WallLineGenTab()
		{
			this.InitializeUI();
		}

		// Token: 0x06000042 RID: 66 RVA: 0x00004E14 File Offset: 0x00003014
		private void InitializeUI()
		{
			base.Padding = new Padding(8);
			this.AutoScroll = true;
			TableLayoutPanel mainLayout = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				ColumnCount = 4,
				RowCount = 12,
				AutoSize = true
			};
			mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40f));
			mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60f));
			int row = 0;
			mainLayout.Controls.Add(this.CreateLabel("Wall thickness\n(mm):"), 0, row);
			this._nudWallThickness = this.CreateNumericUpDown(200m, 50m, 1000m, 0);
			mainLayout.Controls.Add(this._nudWallThickness, 1, row);
			row++;
			mainLayout.Controls.Add(this.CreateLabel("Layer to detect? :"), 0, row);
			this._txtLayerDetect = new TextBox
			{
				Text = "C-WALL",
				Dock = DockStyle.Fill
			};
			mainLayout.Controls.Add(this._txtLayerDetect, 1, row);
			mainLayout.SetColumnSpan(this._txtLayerDetect, 3);
			row++;
			mainLayout.Controls.Add(this.CreateLabel("Angle tolerance\n(degree):"), 0, row);
			this._nudAngleTolerance = this.CreateNumericUpDown(5m, 0m, 45m, 1);
			mainLayout.Controls.Add(this._nudAngleTolerance, 1, row);
			FlowLayoutPanel axesPanel = new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				WrapContents = false
			};
			this._btnPickAxes = new Button
			{
				Text = "Pick",
				Width = 50
			};
			this._btnClearAxes = new Button
			{
				Text = "[ x ]",
				Width = 40
			};
			this._btnPickAxes.Click += this.BtnPickAxes_Click;
			this._btnClearAxes.Click += this.BtnClearAxes_Click;
			axesPanel.Controls.Add(this._btnPickAxes);
			axesPanel.Controls.Add(this._btnClearAxes);
			mainLayout.Controls.Add(axesPanel, 3, row);
			row++;
			mainLayout.Controls.Add(this.CreateLabel("Axes Line:"), 0, row);
			this._txtAxesLine = new TextBox
			{
				Text = "No axes selected\n(optional)",
				Dock = DockStyle.Fill,
				ReadOnly = true,
				ForeColor = Color.Gray,
				Multiline = true,
				Height = 40
			};
			mainLayout.Controls.Add(this._txtAxesLine, 1, row);
			mainLayout.SetColumnSpan(this._txtAxesLine, 2);
			FlowLayoutPanel axesPanel2 = new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				WrapContents = false
			};
			Button btnPickAxes2 = new Button
			{
				Text = "Pick",
				Width = 50
			};
			Button btnClearAxes2 = new Button
			{
				Text = "[ x ]",
				Width = 40
			};
			btnPickAxes2.Click += this.BtnPickAxes_Click;
			btnClearAxes2.Click += this.BtnClearAxes_Click;
			axesPanel2.Controls.Add(btnPickAxes2);
			axesPanel2.Controls.Add(btnClearAxes2);
			mainLayout.Controls.Add(axesPanel2, 3, row);
			row++;
			mainLayout.Controls.Add(this.CreateLabel("Door, Windows\nwidths (mm):"), 0, row);
			this._txtDoorWidths = new TextBox
			{
				Text = "2000,700,750,900,1200,1000,1300,3000",
				Dock = DockStyle.Fill
			};
			mainLayout.Controls.Add(this._txtDoorWidths, 1, row);
			mainLayout.SetColumnSpan(this._txtDoorWidths, 3);
			row++;
			mainLayout.Controls.Add(this.CreateLabel("Columns widths\n(mm):"), 0, row);
			this._txtColumnWidths = new TextBox
			{
				Text = "800,400",
				Dock = DockStyle.Fill
			};
			mainLayout.Controls.Add(this._txtColumnWidths, 1, row);
			mainLayout.SetColumnSpan(this._txtColumnWidths, 3);
			row++;
			mainLayout.Controls.Add(this.CreateLabel("Extend coefficient\n(times):"), 0, row);
			this._nudExtendCoeff = this.CreateNumericUpDown(2m, 0m, 10m, 1);
			mainLayout.Controls.Add(this._nudExtendCoeff, 1, row);
			this._chkAutoExtend = new CheckBox
			{
				Text = "Automatically extend\nperpendicular walls",
				Checked = true,
				AutoSize = true
			};
			mainLayout.Controls.Add(this._chkAutoExtend, 2, row);
			mainLayout.SetColumnSpan(this._chkAutoExtend, 2);
			row++;
			mainLayout.Controls.Add(this.CreateLabel("Wall thk.\ntolerance (mm):"), 0, row);
			this._nudWallThkTolerance = this.CreateNumericUpDown(5m, 0m, 50m, 0);
			mainLayout.Controls.Add(this._nudWallThkTolerance, 1, row);
			this._chkCreateIntersection = new CheckBox
			{
				Text = "Create intersection\nat intersection",
				Checked = true,
				AutoSize = true
			};
			mainLayout.Controls.Add(this._chkCreateIntersection, 2, row);
			mainLayout.SetColumnSpan(this._chkCreateIntersection, 2);
			row++;
			mainLayout.Controls.Add(this.CreateLabel("Auto Join Gap /\nAxis Snap (mm):"), 0, row);
			this._nudAutoJoinGap = this.CreateNumericUpDown(400m, 0m, 2000m, 0);
			mainLayout.Controls.Add(this._nudAutoJoinGap, 1, row);
			this._nudAxisSnap = this.CreateNumericUpDown(500m, 0m, 2000m, 0);
			mainLayout.Controls.Add(this._nudAxisSnap, 3, row);
			row++;
			this._chkBreakAtGrid = new CheckBox
			{
				Text = "Break lines at grid intersections",
				Checked = true,
				AutoSize = true
			};
			mainLayout.Controls.Add(this._chkBreakAtGrid, 0, row);
			mainLayout.SetColumnSpan(this._chkBreakAtGrid, 2);
			this._chkExtendOnGrid = new CheckBox
			{
				Text = "Also extend line on grid",
				Checked = true,
				AutoSize = true
			};
			mainLayout.Controls.Add(this._chkExtendOnGrid, 2, row);
			mainLayout.SetColumnSpan(this._chkExtendOnGrid, 2);
			row++;
			Panel separator = new Panel
			{
				Height = 2,
				Dock = DockStyle.Top,
				BackColor = Color.LightGray,
				Margin = new Padding(0, 10, 0, 10)
			};
			mainLayout.Controls.Add(separator, 0, row);
			mainLayout.SetColumnSpan(separator, 4);
			row++;
			FlowLayoutPanel buttonPanel = new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				FlowDirection = FlowDirection.RightToLeft,
				WrapContents = false
			};
			this._btnCancel = new Button
			{
				Text = "Cancel",
				Width = 75,
				Height = 28
			};
			this._btnRun = new Button
			{
				Text = "Run",
				Width = 85,
				Height = 28,
				BackColor = Color.FromArgb(0, 122, 204),
				ForeColor = Color.White,
				FlatStyle = FlatStyle.Flat
			};
			this._btnDefaults = new Button
			{
				Text = "Defaults",
				Width = 75,
				Height = 28
			};
			this._btnCancel.Click += this.BtnCancel_Click;
			this._btnRun.Click += this.BtnRun_Click;
			this._btnDefaults.Click += this.BtnDefaults_Click;
			buttonPanel.Controls.Add(this._btnCancel);
			buttonPanel.Controls.Add(this._btnRun);
			buttonPanel.Controls.Add(this._btnDefaults);
			mainLayout.Controls.Add(buttonPanel, 0, row);
			mainLayout.SetColumnSpan(buttonPanel, 4);
			base.Controls.Add(mainLayout);
		}

		// Token: 0x06000043 RID: 67 RVA: 0x00005668 File Offset: 0x00003868
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

		// Token: 0x06000044 RID: 68 RVA: 0x000056A8 File Offset: 0x000038A8
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

		// Token: 0x06000045 RID: 69 RVA: 0x000056F8 File Offset: 0x000038F8
		private List<double> ParseDoubleList(string text)
		{
			List<double> list = new List<double>();
			bool flag = string.IsNullOrEmpty(text);
			List<double> list2;
			if (flag)
			{
				list2 = list;
			}
			else
			{
				foreach (string s in text.Split(new char[] { ',' }))
				{
					double d;
					bool flag2 = double.TryParse(s.Trim(), out d);
					if (flag2)
					{
						list.Add(d);
					}
				}
				list2 = list;
			}
			return list2;
		}

		// Token: 0x06000046 RID: 70 RVA: 0x0000576C File Offset: 0x0000396C
		private void BtnPickAxes_Click(object sender, EventArgs e)
		{
			Form parentForm = base.FindForm();
			bool flag = parentForm != null;
			if (flag)
			{
				parentForm.WindowState = FormWindowState.Minimized;
			}
			try
			{
				List<ObjectId> lineIds = AcadUtils.SelectObjectsOnScreen("LINE");
				this._selectedAxes.Clear();
				AcadUtils.UsingTransaction(delegate(Transaction tr)
				{
					foreach (ObjectId lineId in lineIds)
					{
						Line line = tr.GetObject(lineId, 0) as Line;
						bool flag3 = line != null;
						if (flag3)
						{
							this._selectedAxes.Add(new AxisLine(new Point2D(line.StartPoint.X, line.StartPoint.Y), new Point2D(line.EndPoint.X, line.EndPoint.Y), lineId.Handle.ToString()));
						}
					}
				});
				this._txtAxesLine.Text = ((this._selectedAxes.Count > 0) ? string.Format("{0} axes selected", this._selectedAxes.Count) : "No axes selected\n(optional)");
				this._txtAxesLine.ForeColor = ((this._selectedAxes.Count > 0) ? Color.Black : Color.Gray);
			}
			finally
			{
				bool flag2 = parentForm != null;
				if (flag2)
				{
					parentForm.WindowState = FormWindowState.Normal;
				}
			}
		}

		// Token: 0x06000047 RID: 71 RVA: 0x00005858 File Offset: 0x00003A58
		private void BtnClearAxes_Click(object sender, EventArgs e)
		{
			this._selectedAxes.Clear();
			this._txtAxesLine.Text = "No axes selected\n(optional)";
			this._txtAxesLine.ForeColor = Color.Gray;
		}

		// Token: 0x06000048 RID: 72 RVA: 0x0000588C File Offset: 0x00003A8C
		private void BtnDefaults_Click(object sender, EventArgs e)
		{
			this._nudWallThickness.Value = 200m;
			this._txtLayerDetect.Text = "C-WALL";
			this._nudAngleTolerance.Value = 5m;
			this._txtDoorWidths.Text = "2000,700,750,900,1200,1000,1300,3000";
			this._txtColumnWidths.Text = "800,400";
			this._nudExtendCoeff.Value = 2m;
			this._chkAutoExtend.Checked = true;
			this._nudWallThkTolerance.Value = 5m;
			this._chkCreateIntersection.Checked = true;
			this._nudAutoJoinGap.Value = 400m;
			this._nudAxisSnap.Value = 500m;
			this._chkBreakAtGrid.Checked = true;
			this._chkExtendOnGrid.Checked = true;
			this.BtnClearAxes_Click(sender, e);
		}

		// Token: 0x06000049 RID: 73 RVA: 0x00005984 File Offset: 0x00003B84
		private void BtnRun_Click(object sender, EventArgs e)
		{
			try
			{
				Form parentForm = base.FindForm();
				bool flag = parentForm != null;
				if (flag)
				{
					parentForm.WindowState = FormWindowState.Minimized;
				}
				WallSegmentProcessor processor = new WallSegmentProcessor
				{
					WallThicknesses = new List<double> { (double)this._nudWallThickness.Value },
					DoorWidths = this.ParseDoubleList(this._txtDoorWidths.Text),
					ColumnWidths = this.ParseDoubleList(this._txtColumnWidths.Text),
					AngleTolerance = (double)this._nudAngleTolerance.Value,
					DistanceTolerance = (double)this._nudWallThkTolerance.Value,
					AxisSnapDistance = (double)this._nudAxisSnap.Value,
					AutoJoinGapDistance = (double)this._nudAutoJoinGap.Value,
					EnableAutoExtend = this._chkAutoExtend.Checked,
					BreakAtGridIntersections = this._chkBreakAtGrid.Checked,
					ExtendToGridIntersections = this._chkExtendOnGrid.Checked
				};
				string layer = this._txtLayerDetect.Text.Trim();
				List<ObjectId> lineIds = AcadUtils.SelectObjectsOnScreen("LINE");
				bool flag2 = lineIds.Count == 0;
				if (flag2)
				{
					MessageBox.Show("No lines selected!", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
				}
				else
				{
					List<WallSegment> segments = new List<WallSegment>();
					AcadUtils.UsingTransaction(delegate(Transaction tr)
					{
						foreach (ObjectId lineId in lineIds)
						{
							Line line = tr.GetObject(lineId, 0) as Line;
							bool flag4 = line == null;
							if (!flag4)
							{
								bool flag5 = !string.IsNullOrEmpty(layer) && !line.Layer.Equals(layer, StringComparison.OrdinalIgnoreCase);
								if (!flag5)
								{
									segments.Add(new WallSegment(new Point2D(line.StartPoint.X, line.StartPoint.Y), new Point2D(line.EndPoint.X, line.EndPoint.Y))
									{
										Handle = lineId.Handle.ToString(),
										Layer = line.Layer
									});
								}
							}
						}
					});
					List<CenterLine> centerlines = processor.Process(segments, this._selectedAxes);
					AcadUtils.CreateLayer("dts_centerlines", 4);
					AcadUtils.ClearLayer("dts_centerlines");
					AcadUtils.UsingTransaction(delegate(Transaction tr)
					{
						foreach (CenterLine cl in centerlines)
						{
							AcadUtils.CreateLine(cl.AsSegment, "dts_centerlines", 4, tr);
						}
					});
					MessageBox.Show(string.Concat(new string[]
					{
						"Processing complete!\n\n",
						string.Format("Input segments: {0}\n", segments.Count),
						string.Format("Merged: {0}\n", processor.MergedSegmentsCount),
						string.Format("Pairs detected: {0}\n", processor.DetectedPairsCount),
						string.Format("Gaps recovered: {0}\n", processor.RecoveredGapsCount),
						string.Format("Output centerlines: {0}", centerlines.Count)
					}), "Success", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				}
			}
			catch (Exception ex)
			{
				MessageBox.Show("Error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
			finally
			{
				Form parentForm2 = base.FindForm();
				bool flag3 = parentForm2 != null;
				if (flag3)
				{
					parentForm2.WindowState = FormWindowState.Normal;
				}
			}
		}

		// Token: 0x0600004A RID: 74 RVA: 0x00005C74 File Offset: 0x00003E74
		private void BtnCancel_Click(object sender, EventArgs e)
		{
			Form form = base.FindForm();
			if (form != null)
			{
				form.Close();
			}
		}

		// Token: 0x0600004B RID: 75 RVA: 0x00005C89 File Offset: 0x00003E89
		public void LoadSettings()
		{
		}

		// Token: 0x0600004C RID: 76 RVA: 0x00005C8C File Offset: 0x00003E8C
		public void SaveSettings()
		{
		}

		// Token: 0x0600004D RID: 77 RVA: 0x00005C8F File Offset: 0x00003E8F
		private void InitializeComponent()
		{
			base.SuspendLayout();
			base.Name = "WallLineGenTab";
			base.Size = new Size(415, 515);
			base.ResumeLayout(false);
		}

		// Token: 0x04000047 RID: 71
		private NumericUpDown _nudWallThickness;

		// Token: 0x04000048 RID: 72
		private TextBox _txtLayerDetect;

		// Token: 0x04000049 RID: 73
		private NumericUpDown _nudAngleTolerance;

		// Token: 0x0400004A RID: 74
		private TextBox _txtAxesLine;

		// Token: 0x0400004B RID: 75
		private Button _btnPickAxes;

		// Token: 0x0400004C RID: 76
		private Button _btnClearAxes;

		// Token: 0x0400004D RID: 77
		private TextBox _txtDoorWidths;

		// Token: 0x0400004E RID: 78
		private TextBox _txtColumnWidths;

		// Token: 0x0400004F RID: 79
		private NumericUpDown _nudExtendCoeff;

		// Token: 0x04000050 RID: 80
		private CheckBox _chkAutoExtend;

		// Token: 0x04000051 RID: 81
		private NumericUpDown _nudWallThkTolerance;

		// Token: 0x04000052 RID: 82
		private CheckBox _chkCreateIntersection;

		// Token: 0x04000053 RID: 83
		private NumericUpDown _nudAutoJoinGap;

		// Token: 0x04000054 RID: 84
		private NumericUpDown _nudAxisSnap;

		// Token: 0x04000055 RID: 85
		private CheckBox _chkBreakAtGrid;

		// Token: 0x04000056 RID: 86
		private CheckBox _chkExtendOnGrid;

		// Token: 0x04000057 RID: 87
		private Button _btnDefaults;

		// Token: 0x04000058 RID: 88
		private Button _btnRun;

		// Token: 0x04000059 RID: 89
		private Button _btnCancel;

		// Token: 0x0400005A RID: 90
		private List<AxisLine> _selectedAxes = new List<AxisLine>();
	}
}
