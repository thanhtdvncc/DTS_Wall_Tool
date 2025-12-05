using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Autodesk.AutoCAD.DatabaseServices;
using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Utils;

namespace DTS_Wall_Tool.UI.Forms
{
	// Token: 0x02000008 RID: 8
	public class LoadAssignmentTab : UserControl
	{
		// Token: 0x0600002D RID: 45 RVA: 0x00003C34 File Offset: 0x00001E34
		public LoadAssignmentTab()
		{
			this.InitializeUI();
		}

		// Token: 0x0600002E RID: 46 RVA: 0x00003C50 File Offset: 0x00001E50
		private void InitializeUI()
		{
			base.Padding = new Padding(8);
			TableLayoutPanel mainLayout = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				ColumnCount = 1,
				RowCount = 4
			};
			mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100f));
			mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
			mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35f));
			mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));
			this._grpStorySelection = new GroupBox
			{
				Text = "Story Selection:",
				Dock = DockStyle.Fill
			};
			TableLayoutPanel storyLayout = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				ColumnCount = 4,
				RowCount = 3,
				Padding = new Padding(5)
			};
			this._lstStories = new ListBox
			{
				Height = 60,
				Width = 150
			};
			storyLayout.Controls.Add(this._lstStories, 0, 0);
			storyLayout.SetRowSpan(this._lstStories, 3);
			this._btnLoadStories = new Button
			{
				Text = "Load Stories",
				Width = 80,
				Height = 25
			};
			this._btnPickPoint = new Button
			{
				Text = "Pick Point",
				Width = 80,
				Height = 25
			};
			this._btnLoadStories.Click += this.BtnLoadStories_Click;
			this._btnPickPoint.Click += this.BtnPickPoint_Click;
			storyLayout.Controls.Add(this._btnLoadStories, 1, 0);
			storyLayout.Controls.Add(this._btnPickPoint, 1, 1);
			FlowLayoutPanel elevPanel = new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				WrapContents = false
			};
			elevPanel.Controls.Add(new Label
			{
				Text = "E(mm):",
				AutoSize = true,
				Margin = new Padding(3, 6, 0, 0)
			});
			this._nudElevation = new NumericUpDown
			{
				Width = 70,
				Minimum = -100000m,
				Maximum = 100000m
			};
			elevPanel.Controls.Add(this._nudElevation);
			elevPanel.Controls.Add(new Label
			{
				Text = "H(mm):",
				AutoSize = true,
				Margin = new Padding(10, 6, 0, 0)
			});
			this._nudHeight = new NumericUpDown
			{
				Width = 70,
				Minimum = 0m,
				Maximum = 100000m,
				Value = 3300m
			};
			elevPanel.Controls.Add(this._nudHeight);
			storyLayout.Controls.Add(elevPanel, 2, 0);
			storyLayout.SetColumnSpan(elevPanel, 2);
			FlowLayoutPanel originPanel = new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				WrapContents = false
			};
			originPanel.Controls.Add(new Label
			{
				Text = "X(mm):",
				AutoSize = true,
				Margin = new Padding(3, 6, 0, 0)
			});
			this._nudOriginX = new NumericUpDown
			{
				Width = 80,
				Minimum = -1000000m,
				Maximum = 1000000m
			};
			originPanel.Controls.Add(this._nudOriginX);
			originPanel.Controls.Add(new Label
			{
				Text = "Y(mm):",
				AutoSize = true,
				Margin = new Padding(10, 6, 0, 0)
			});
			this._nudOriginY = new NumericUpDown
			{
				Width = 80,
				Minimum = -1000000m,
				Maximum = 1000000m
			};
			originPanel.Controls.Add(this._nudOriginY);
			storyLayout.Controls.Add(new Label
			{
				Text = "Set Model Origin\nin AutoCAD:",
				AutoSize = true
			}, 1, 2);
			storyLayout.Controls.Add(originPanel, 2, 2);
			storyLayout.SetColumnSpan(originPanel, 2);
			this._grpStorySelection.Controls.Add(storyLayout);
			mainLayout.Controls.Add(this._grpStorySelection, 0, 0);
			this._grpWallLoads = new GroupBox
			{
				Text = "Wall Load Assignments",
				Dock = DockStyle.Fill
			};
			TableLayoutPanel loadLayout = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				ColumnCount = 2,
				RowCount = 1,
				Padding = new Padding(5)
			};
			loadLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80f));
			loadLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
			FlowLayoutPanel leftPanel = new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				FlowDirection = FlowDirection.TopDown,
				WrapContents = false
			};
			this._btnGetAcad = new Button
			{
				Text = "Get Acad",
				Width = 70,
				Height = 28,
				Margin = new Padding(2)
			};
			this._btnSyncToCad = new Button
			{
				Text = "Sync to CAD",
				Width = 70,
				Height = 28,
				Margin = new Padding(2)
			};
			this._btnSyncFromSap = new Button
			{
				Text = "Sync fr.  SAP",
				Width = 70,
				Height = 28,
				Margin = new Padding(2)
			};
			this._btnGetAcad.Click += this.BtnGetAcad_Click;
			this._btnSyncToCad.Click += this.BtnSyncToCad_Click;
			this._btnSyncFromSap.Click += this.BtnSyncFromSap_Click;
			leftPanel.Controls.Add(this._btnGetAcad);
			leftPanel.Controls.Add(this._btnSyncToCad);
			leftPanel.Controls.Add(this._btnSyncFromSap);
			loadLayout.Controls.Add(leftPanel, 0, 0);
			Panel listPanel = new Panel
			{
				Dock = DockStyle.Fill
			};
			this._lstWallLoads = new ListView
			{
				Dock = DockStyle.Fill,
				View = View.Details,
				FullRowSelect = true,
				GridLines = true,
				Font = new Font("Consolas", 9f)
			};
			this._lstWallLoads.Columns.Add("#", 30);
			this._lstWallLoads.Columns.Add("Handle", 60);
			this._lstWallLoads.Columns.Add("Thick", 50);
			this._lstWallLoads.Columns.Add("Pattern", 50);
			FlowLayoutPanel rightPanel = new FlowLayoutPanel
			{
				Dock = DockStyle.Right,
				Width = 50,
				FlowDirection = FlowDirection.TopDown,
				WrapContents = false
			};
			this._btnMoveUp = new Button
			{
				Text = "[=>",
				Width = 40,
				Height = 25
			};
			this._btnMoveDown = new Button
			{
				Text = "<=]",
				Width = 40,
				Height = 25
			};
			this._btnRemoveItem = new Button
			{
				Text = "[x]",
				Width = 40,
				Height = 25
			};
			rightPanel.Controls.Add(this._btnMoveUp);
			rightPanel.Controls.Add(this._btnMoveDown);
			rightPanel.Controls.Add(this._btnRemoveItem);
			listPanel.Controls.Add(this._lstWallLoads);
			listPanel.Controls.Add(rightPanel);
			loadLayout.Controls.Add(listPanel, 1, 0);
			this._grpWallLoads.Controls.Add(loadLayout);
			mainLayout.Controls.Add(this._grpWallLoads, 0, 1);
			FlowLayoutPanel middlePanel = new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				WrapContents = false
			};
			this._btnSetOverwrite = new Button
			{
				Text = "Set Overwrite",
				Width = 85,
				Height = 28
			};
			this._btnSetStories = new Button
			{
				Text = "Set Stories",
				Width = 75,
				Height = 28
			};
			this._btnDelLabel = new Button
			{
				Text = "Del Label",
				Width = 70,
				Height = 28
			};
			this._btnShowLabel = new Button
			{
				Text = "Show Label",
				Width = 80,
				Height = 28
			};
			middlePanel.Controls.Add(this._btnSetOverwrite);
			middlePanel.Controls.Add(this._btnSetStories);
			middlePanel.Controls.Add(this._btnDelLabel);
			middlePanel.Controls.Add(this._btnShowLabel);
			mainLayout.Controls.Add(middlePanel, 0, 2);
			FlowLayoutPanel bottomPanel = new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				WrapContents = false
			};
			this._btnDeleteLoad = new Button
			{
				Text = "Delete load",
				Width = 80,
				Height = 28
			};
			this._btnClearSapLoad = new Button
			{
				Text = "ClearSAP load",
				Width = 95,
				Height = 28
			};
			this._btnAssignLoads = new Button
			{
				Text = "Assign Loads",
				Width = 95,
				Height = 28,
				BackColor = Color.FromArgb(0, 122, 204),
				ForeColor = Color.White,
				FlatStyle = FlatStyle.Flat
			};
			this._btnCancel = new Button
			{
				Text = "Cancel",
				Width = 70,
				Height = 28
			};
			this._btnAssignLoads.Click += this.BtnAssignLoads_Click;
			this._btnCancel.Click += this.BtnCancel_Click;
			bottomPanel.Controls.Add(this._btnDeleteLoad);
			bottomPanel.Controls.Add(this._btnClearSapLoad);
			bottomPanel.Controls.Add(this._btnAssignLoads);
			bottomPanel.Controls.Add(this._btnCancel);
			mainLayout.Controls.Add(bottomPanel, 0, 3);
			base.Controls.Add(mainLayout);
		}

		// Token: 0x0600002F RID: 47 RVA: 0x00004710 File Offset: 0x00002910
		private void BtnLoadStories_Click(object sender, EventArgs e)
		{
			try
			{
				bool flag = !SapUtils.IsConnected;
				if (flag)
				{
					string msg;
					SapUtils.Connect(out msg);
				}
				List<SapUtils.GridStoryItem> stories = SapUtils.GetStories();
				this._lstStories.Items.Clear();
				foreach (SapUtils.GridStoryItem story in stories)
				{
					this._lstStories.Items.Add(string.Format("{0} (Z={1})", story.StoryName, story.Elevation));
				}
			}
			catch (Exception ex)
			{
				MessageBox.Show("Error loading stories: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		}

		// Token: 0x06000030 RID: 48 RVA: 0x000047F0 File Offset: 0x000029F0
		private void BtnPickPoint_Click(object sender, EventArgs e)
		{
			MessageBox.Show("Pick point from AutoCAD - Coming soon", "Info", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
		}

		// Token: 0x06000031 RID: 49 RVA: 0x00004808 File Offset: 0x00002A08
		private void BtnGetAcad_Click(object sender, EventArgs e)
		{
			try
			{
				Form parentForm = base.FindForm();
				bool flag = parentForm != null;
				if (flag)
				{
					parentForm.WindowState = FormWindowState.Minimized;
				}
				List<ObjectId> lineIds = AcadUtils.SelectObjectsOnScreen("LINE");
				this._wallLoadItems.Clear();
				this._lstWallLoads.Items.Clear();
				int index = 1;
				AcadUtils.UsingTransaction(delegate(Transaction tr)
				{
					foreach (ObjectId lineId in lineIds)
					{
						DBObject obj = tr.GetObject(lineId, 0);
						WallData wData = XDataUtils.ReadWallData(obj);
						LoadAssignmentTab.WallLoadItem wallLoadItem = new LoadAssignmentTab.WallLoadItem();
						int index2 = index;
						index = index2 + 1;
						wallLoadItem.Index = index2;
						wallLoadItem.Handle = lineId.Handle.ToString();
						double? num;
						double? num2;
						if (wData == null)
						{
							num = null;
							num2 = num;
						}
						else
						{
							num2 = wData.Thickness;
						}
						num = num2;
						wallLoadItem.Thickness = new double?(num.GetValueOrDefault());
						wallLoadItem.LoadPattern = ((wData != null) ? wData.GetPrimaryLoadPattern() : null) ?? "DL";
						LoadAssignmentTab.WallLoadItem item = wallLoadItem;
						this._wallLoadItems.Add(item);
						ListViewItem lvItem = new ListViewItem(item.Index.ToString());
						lvItem.SubItems.Add(item.Handle);
						ListViewItem.ListViewSubItemCollection subItems = lvItem.SubItems;
						num = item.Thickness;
						subItems.Add(((num != null) ? num.GetValueOrDefault().ToString() : null) ?? "");
						lvItem.SubItems.Add(item.LoadPattern);
						this._lstWallLoads.Items.Add(lvItem);
					}
				});
			}
			finally
			{
				Form parentForm2 = base.FindForm();
				bool flag2 = parentForm2 != null;
				if (flag2)
				{
					parentForm2.WindowState = FormWindowState.Normal;
				}
			}
		}

		// Token: 0x06000032 RID: 50 RVA: 0x000048B0 File Offset: 0x00002AB0
		private void BtnSyncToCad_Click(object sender, EventArgs e)
		{
		}

		// Token: 0x06000033 RID: 51 RVA: 0x000048B3 File Offset: 0x00002AB3
		private void BtnSyncFromSap_Click(object sender, EventArgs e)
		{
		}

		// Token: 0x06000034 RID: 52 RVA: 0x000048B8 File Offset: 0x00002AB8
		private void BtnAssignLoads_Click(object sender, EventArgs e)
		{
			try
			{
				bool flag = !SapUtils.IsConnected;
				if (flag)
				{
					string msg;
					bool flag2 = !SapUtils.Connect(out msg);
					if (flag2)
					{
						MessageBox.Show(msg, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
						return;
					}
				}
				int assigned = 0;
				int failed = 0;
				foreach (LoadAssignmentTab.WallLoadItem item in this._wallLoadItems)
				{
					assigned++;
				}
				MessageBox.Show(string.Format("Assigned: {0}, Failed: {1}", assigned, failed), "Result", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			}
			catch (Exception ex)
			{
				MessageBox.Show("Error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		}

		// Token: 0x06000035 RID: 53 RVA: 0x000049A0 File Offset: 0x00002BA0
		private void BtnCancel_Click(object sender, EventArgs e)
		{
			Form form = base.FindForm();
			if (form != null)
			{
				form.Close();
			}
		}

		// Token: 0x06000036 RID: 54 RVA: 0x000049B5 File Offset: 0x00002BB5
		public void LoadSettings()
		{
		}

		// Token: 0x06000037 RID: 55 RVA: 0x000049B8 File Offset: 0x00002BB8
		public void SaveSettings()
		{
		}

		// Token: 0x04000026 RID: 38
		private GroupBox _grpStorySelection;

		// Token: 0x04000027 RID: 39
		private ListBox _lstStories;

		// Token: 0x04000028 RID: 40
		private Button _btnLoadStories;

		// Token: 0x04000029 RID: 41
		private Button _btnPickPoint;

		// Token: 0x0400002A RID: 42
		private NumericUpDown _nudElevation;

		// Token: 0x0400002B RID: 43
		private NumericUpDown _nudHeight;

		// Token: 0x0400002C RID: 44
		private NumericUpDown _nudOriginX;

		// Token: 0x0400002D RID: 45
		private NumericUpDown _nudOriginY;

		// Token: 0x0400002E RID: 46
		private GroupBox _grpWallLoads;

		// Token: 0x0400002F RID: 47
		private ListView _lstWallLoads;

		// Token: 0x04000030 RID: 48
		private Button _btnGetAcad;

		// Token: 0x04000031 RID: 49
		private Button _btnSyncToCad;

		// Token: 0x04000032 RID: 50
		private Button _btnSyncFromSap;

		// Token: 0x04000033 RID: 51
		private Button _btnMoveUp;

		// Token: 0x04000034 RID: 52
		private Button _btnMoveDown;

		// Token: 0x04000035 RID: 53
		private Button _btnRemoveItem;

		// Token: 0x04000036 RID: 54
		private Button _btnSetOverwrite;

		// Token: 0x04000037 RID: 55
		private Button _btnSetStories;

		// Token: 0x04000038 RID: 56
		private Button _btnDelLabel;

		// Token: 0x04000039 RID: 57
		private Button _btnShowLabel;

		// Token: 0x0400003A RID: 58
		private Button _btnDeleteLoad;

		// Token: 0x0400003B RID: 59
		private Button _btnClearSapLoad;

		// Token: 0x0400003C RID: 60
		private Button _btnAssignLoads;

		// Token: 0x0400003D RID: 61
		private Button _btnCancel;

		// Token: 0x0400003E RID: 62
		private static readonly List<LoadAssignmentTab.WallLoadItem> wallLoadItems = new List<LoadAssignmentTab.WallLoadItem>();

		// Token: 0x0400003F RID: 63
		private List<LoadAssignmentTab.WallLoadItem> _wallLoadItems = LoadAssignmentTab.wallLoadItems;

		// Token: 0x0200005E RID: 94
		private class WallLoadItem
		{
			// Token: 0x1700015C RID: 348
			// (get) Token: 0x0600049B RID: 1179 RVA: 0x0001E928 File Offset: 0x0001CB28
			// (set) Token: 0x0600049C RID: 1180 RVA: 0x0001E930 File Offset: 0x0001CB30
			public int Index { get; set; }

			// Token: 0x1700015D RID: 349
			// (get) Token: 0x0600049D RID: 1181 RVA: 0x0001E939 File Offset: 0x0001CB39
			// (set) Token: 0x0600049E RID: 1182 RVA: 0x0001E941 File Offset: 0x0001CB41
			public string Handle { get; set; }

			// Token: 0x1700015E RID: 350
			// (get) Token: 0x0600049F RID: 1183 RVA: 0x0001E94A File Offset: 0x0001CB4A
			// (set) Token: 0x060004A0 RID: 1184 RVA: 0x0001E952 File Offset: 0x0001CB52
			public double? Thickness { get; set; }

			// Token: 0x1700015F RID: 351
			// (get) Token: 0x060004A1 RID: 1185 RVA: 0x0001E95B File Offset: 0x0001CB5B
			// (set) Token: 0x060004A2 RID: 1186 RVA: 0x0001E963 File Offset: 0x0001CB63
			public string LoadPattern { get; set; }
		}
	}
}
