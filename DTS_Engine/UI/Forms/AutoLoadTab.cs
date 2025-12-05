using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using DTS_Wall_Tool.Core.Engines;
using DTS_Wall_Tool.Core.Interfaces;

namespace DTS_Wall_Tool.UI.Forms
{
	// Token: 0x02000007 RID: 7
	public class AutoLoadTab : UserControl
	{
		// Token: 0x06000024 RID: 36 RVA: 0x00002997 File Offset: 0x00000B97
		public AutoLoadTab()
		{
			this._calculator = new LoadCalculator();
			this.InitializeUI();
		}

		// Token: 0x06000025 RID: 37 RVA: 0x000029BE File Offset: 0x00000BBE
		public AutoLoadTab(LoadCalculator calculator)
		{
			this._calculator = calculator ?? new LoadCalculator();
			this.InitializeUI();
		}

		// Token: 0x06000026 RID: 38 RVA: 0x000029EC File Offset: 0x00000BEC
		private void InitializeUI()
		{
			base.Padding = new Padding(8);
			this.AutoScroll = true;
			TableLayoutPanel mainLayout = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				ColumnCount = 1,
				RowCount = 5
			};
			mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90f));
			mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 40f));
			mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90f));
			mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 30f));
			mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));
			this._grpLoadMethod = new GroupBox
			{
				Text = "Load Calculation Method:",
				Dock = DockStyle.Fill
			};
			TableLayoutPanel methodLayout = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				ColumnCount = 4,
				RowCount = 3,
				Padding = new Padding(5)
			};
			this._rdoAreaLoad = new RadioButton
			{
				Text = "Area Load (kN/m²) - For Slab/Shell",
				AutoSize = true
			};
			this._rdoLineLoad = new RadioButton
			{
				Text = "Line Load (kN/m) - For Wall/Beam",
				AutoSize = true,
				Checked = true
			};
			methodLayout.Controls.Add(this._rdoAreaLoad, 0, 0);
			methodLayout.SetColumnSpan(this._rdoAreaLoad, 4);
			methodLayout.Controls.Add(this._rdoLineLoad, 0, 1);
			methodLayout.SetColumnSpan(this._rdoLineLoad, 4);
			FlowLayoutPanel factorPanel = new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				WrapContents = false
			};
			factorPanel.Controls.Add(new Label
			{
				Text = "Load Factor:",
				AutoSize = true,
				Margin = new Padding(0, 6, 5, 0)
			});
			this._nudLoadFactor = new NumericUpDown
			{
				Width = 60,
				Minimum = 0.1m,
				Maximum = 10m,
				DecimalPlaces = 2,
				Value = 1m,
				Increment = 0.1m
			};
			factorPanel.Controls.Add(this._nudLoadFactor);
			factorPanel.Controls.Add(new Label
			{
				Text = "(Global multiplier)",
				AutoSize = true,
				Margin = new Padding(10, 6, 0, 0),
				ForeColor = Color.Gray
			});
			FlowLayoutPanel factorBtnPanel = new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				WrapContents = false
			};
			factorBtnPanel.Controls.Add(new Button
			{
				Text = "[ + ]",
				Width = 40,
				Height = 25
			});
			factorBtnPanel.Controls.Add(new Button
			{
				Text = "[ - ]",
				Width = 40,
				Height = 25
			});
			methodLayout.Controls.Add(factorPanel, 0, 2);
			methodLayout.SetColumnSpan(factorPanel, 2);
			methodLayout.Controls.Add(factorBtnPanel, 3, 2);
			this._chkAutoDeductBeam = new CheckBox
			{
				Text = "Auto deduct beam depth from height",
				AutoSize = true
			};
			methodLayout.Controls.Add(this._chkAutoDeductBeam, 2, 2);
			this._grpLoadMethod.Controls.Add(methodLayout);
			mainLayout.Controls.Add(this._grpLoadMethod, 0, 0);
			Panel gridPanel = new Panel
			{
				Dock = DockStyle.Fill
			};
			this._lstStoryLoads = new ListView
			{
				Dock = DockStyle.Fill,
				View = View.Details,
				FullRowSelect = true,
				GridLines = true,
				Font = new Font("Consolas", 9f)
			};
			this._lstStoryLoads.Columns.Add("Story", 60);
			this._lstStoryLoads.Columns.Add("Type", 50);
			this._lstStoryLoads.Columns.Add("Thick", 50);
			this._lstStoryLoads.Columns.Add("Height", 70);
			this._lstStoryLoads.Columns.Add("Load", 70);
			this.AddSampleStoryData();
			FlowLayoutPanel gridBtnPanel = new FlowLayoutPanel
			{
				Dock = DockStyle.Right,
				Width = 50,
				FlowDirection = FlowDirection.TopDown,
				WrapContents = false,
				Padding = new Padding(5)
			};
			this._btnMoveToTop = new Button
			{
				Text = ">>",
				Width = 35,
				Height = 25
			};
			this._btnMoveUp = new Button
			{
				Text = "=>",
				Width = 35,
				Height = 25
			};
			this._btnMoveDown = new Button
			{
				Text = "<=",
				Width = 35,
				Height = 25
			};
			this._btnMoveToBottom = new Button
			{
				Text = "<<",
				Width = 35,
				Height = 25
			};
			gridBtnPanel.Controls.Add(this._btnMoveToTop);
			gridBtnPanel.Controls.Add(this._btnMoveUp);
			gridBtnPanel.Controls.Add(this._btnMoveDown);
			gridBtnPanel.Controls.Add(this._btnMoveToBottom);
			gridPanel.Controls.Add(this._lstStoryLoads);
			gridPanel.Controls.Add(gridBtnPanel);
			FlowLayoutPanel actionPanel = new FlowLayoutPanel
			{
				Dock = DockStyle.Left,
				Width = 80,
				FlowDirection = FlowDirection.TopDown,
				WrapContents = false,
				Padding = new Padding(0, 5, 5, 5)
			};
			this._btnGetData = new Button
			{
				Text = "Get Data",
				Width = 70,
				Height = 28,
				Margin = new Padding(0, 2, 0, 2)
			};
			this._btnCalculate = new Button
			{
				Text = "Calculate",
				Width = 70,
				Height = 28,
				BackColor = Color.FromArgb(0, 150, 136),
				ForeColor = Color.White,
				FlatStyle = FlatStyle.Flat,
				Margin = new Padding(0, 2, 0, 2)
			};
			this._btnGetData.Click += this.BtnGetData_Click;
			this._btnCalculate.Click += this.BtnCalculate_Click;
			actionPanel.Controls.Add(this._btnGetData);
			actionPanel.Controls.Add(this._btnCalculate);
			gridPanel.Controls.Add(actionPanel);
			mainLayout.Controls.Add(gridPanel, 0, 1);
			TableLayoutPanel paramPanel = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				ColumnCount = 4,
				RowCount = 3
			};
			paramPanel.Controls.Add(new Label
			{
				Text = "Parapet Height:",
				AutoSize = true
			}, 0, 0);
			this._nudParapetHeight = new NumericUpDown
			{
				Width = 70,
				Value = 1200m,
				Maximum = 5000m
			};
			paramPanel.Controls.Add(this._nudParapetHeight, 1, 0);
			paramPanel.Controls.Add(new Label
			{
				Text = "mm",
				AutoSize = true
			}, 2, 0);
			paramPanel.Controls.Add(new Button
			{
				Text = ">>",
				Width = 35
			}, 3, 0);
			paramPanel.Controls.Add(new Label
			{
				Text = "Roof Wall Height:",
				AutoSize = true
			}, 0, 1);
			this._nudRoofWallHeight = new NumericUpDown
			{
				Width = 70,
				Value = 1500m,
				Maximum = 5000m
			};
			paramPanel.Controls.Add(this._nudRoofWallHeight, 1, 1);
			paramPanel.Controls.Add(new Label
			{
				Text = "mm",
				AutoSize = true
			}, 2, 1);
			paramPanel.Controls.Add(new Label
			{
				Text = "Fire Wall Factor:",
				AutoSize = true
			}, 0, 2);
			this._nudFireWallFactor = new NumericUpDown
			{
				Width = 70,
				Value = 1.2m,
				DecimalPlaces = 2,
				Increment = 0.1m,
				Minimum = 0.1m,
				Maximum = 5m
			};
			paramPanel.Controls.Add(this._nudFireWallFactor, 1, 2);
			mainLayout.Controls.Add(paramPanel, 0, 2);
			this._grpModifiers = new GroupBox
			{
				Text = "Load Modifiers:",
				Dock = DockStyle.Fill
			};
			TableLayoutPanel modLayout = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				ColumnCount = 2,
				RowCount = 1,
				Padding = new Padding(5)
			};
			modLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80f));
			modLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
			FlowLayoutPanel modBtnPanel = new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				FlowDirection = FlowDirection.TopDown,
				WrapContents = false
			};
			this._btnFindModifier = new Button
			{
				Text = "Find",
				Width = 70,
				Height = 25,
				Margin = new Padding(2)
			};
			this._btnClearFind = new Button
			{
				Text = "Clear Find",
				Width = 70,
				Height = 25,
				Margin = new Padding(2)
			};
			modBtnPanel.Controls.Add(this._btnFindModifier);
			modBtnPanel.Controls.Add(this._btnClearFind);
			modLayout.Controls.Add(modBtnPanel, 0, 0);
			Panel modListPanel = new Panel
			{
				Dock = DockStyle.Fill
			};
			this._lstModifiers = new ListView
			{
				Dock = DockStyle.Fill,
				View = View.Details,
				FullRowSelect = true,
				GridLines = true,
				Font = new Font("Consolas", 9f)
			};
			this._lstModifiers.Columns.Add("Name", 100);
			this._lstModifiers.Columns.Add("Value", 60);
			this._lstModifiers.Columns.Add("Opera", 50);
			this._lstModifiers.Columns.Add("Ov", 30);
			foreach (LoadModifier mod in this._calculator.Modifiers)
			{
				ListViewItem modItem = new ListViewItem(mod.Name);
				modItem.SubItems.Add((mod.Type == "HEIGHT_OVERRIDE") ? mod.HeightOverride.ToString() : ((mod.Type == "FACTOR") ? mod.Factor.ToString() : mod.AddValue.ToString()));
				modItem.SubItems.Add(mod.Type);
				modItem.SubItems.Add("-");
				this._lstModifiers.Items.Add(modItem);
			}
			FlowLayoutPanel modRightBtnPanel = new FlowLayoutPanel
			{
				Dock = DockStyle.Right,
				Width = 50,
				FlowDirection = FlowDirection.TopDown,
				WrapContents = false
			};
			this._btnAddModifier = new Button
			{
				Text = "Add",
				Width = 40,
				Height = 25,
				Margin = new Padding(2)
			};
			this._btnDelModifier = new Button
			{
				Text = "Del",
				Width = 40,
				Height = 25,
				Margin = new Padding(2)
			};
			modRightBtnPanel.Controls.Add(this._btnAddModifier);
			modRightBtnPanel.Controls.Add(this._btnDelModifier);
			modListPanel.Controls.Add(this._lstModifiers);
			modListPanel.Controls.Add(modRightBtnPanel);
			modLayout.Controls.Add(modListPanel, 1, 0);
			this._grpModifiers.Controls.Add(modLayout);
			mainLayout.Controls.Add(this._grpModifiers, 0, 3);
			FlowLayoutPanel bottomPanel = new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				WrapContents = false
			};
			this._btnAssignLoad = new Button
			{
				Text = "Assign Load",
				Width = 95,
				Height = 28,
				BackColor = Color.FromArgb(0, 122, 204),
				ForeColor = Color.White,
				FlatStyle = FlatStyle.Flat
			};
			this._btnClearModify = new Button
			{
				Text = "Clear all Modify",
				Width = 100,
				Height = 28
			};
			this._btnGetDataBottom = new Button
			{
				Text = "Get Data",
				Width = 80,
				Height = 28,
				BackColor = Color.FromArgb(76, 175, 80),
				ForeColor = Color.White,
				FlatStyle = FlatStyle.Flat
			};
			this._btnAssignLoad.Click += this.BtnAssignLoad_Click;
			bottomPanel.Controls.Add(this._btnAssignLoad);
			bottomPanel.Controls.Add(this._btnClearModify);
			bottomPanel.Controls.Add(this._btnGetDataBottom);
			mainLayout.Controls.Add(bottomPanel, 0, 4);
			base.Controls.Add(mainLayout);
		}

		// Token: 0x06000027 RID: 39 RVA: 0x00003838 File Offset: 0x00001A38
		private void AddSampleStoryData()
		{
			var data = new <>f__AnonymousType1<string, string, string, string, string>[]
			{
				new
				{
					Story = "500",
					Type = "W200",
					Thick = "200",
					Height = "6500.00",
					Load = ""
				},
				new
				{
					Story = "7000",
					Type = "W200",
					Thick = "200",
					Height = "4500.00",
					Load = ""
				},
				new
				{
					Story = "11500",
					Type = "W200",
					Thick = "200",
					Height = "3600.00",
					Load = ""
				},
				new
				{
					Story = "15100",
					Type = "W200",
					Thick = "200",
					Height = "0.00",
					Load = ""
				}
			};
			var array = data;
			for (int i = 0; i < array.Length; i++)
			{
				var d = array[i];
				ListViewItem item = new ListViewItem(d.Story);
				item.SubItems.Add(d.Type);
				item.SubItems.Add(d.Thick);
				item.SubItems.Add(d.Height);
				item.SubItems.Add(d.Load);
				this._lstStoryLoads.Items.Add(item);
			}
		}

		// Token: 0x06000028 RID: 40 RVA: 0x00003954 File Offset: 0x00001B54
		private void BtnGetData_Click(object sender, EventArgs e)
		{
			MessageBox.Show("Get data from SAP2000 - Coming soon", "Info", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
		}

		// Token: 0x06000029 RID: 41 RVA: 0x0000396C File Offset: 0x00001B6C
		private void BtnCalculate_Click(object sender, EventArgs e)
		{
			try
			{
				this._calculator.LoadFactor = (double)this._nudLoadFactor.Value;
				bool @checked = this._chkAutoDeductBeam.Checked;
				if (@checked)
				{
					this._calculator.BeamHeightDeduction = 400.0;
				}
				else
				{
					this._calculator.BeamHeightDeduction = 0.0;
				}
				foreach (object obj in this._lstStoryLoads.Items)
				{
					ListViewItem item = (ListViewItem)obj;
					double thickness;
					double height;
					bool flag = double.TryParse(item.SubItems[2].Text, out thickness) && double.TryParse(item.SubItems[3].Text, out height);
					if (flag)
					{
						bool checked2 = this._rdoLineLoad.Checked;
						if (checked2)
						{
							double load = this._calculator.CalculateLineLoad(thickness, height, null);
							item.SubItems[4].Text = string.Format("{0:0.00}", load);
						}
						else
						{
							double thickM = thickness / 1000.0;
							double areaLoad = thickM * this._calculator.WallUnitWeight * this._calculator.LoadFactor;
							item.SubItems[4].Text = string.Format("{0:0.00}", areaLoad);
						}
					}
				}
				Dictionary<int, double> loadTable = LoadCalculator.GetQuickLoadTable((double)this._nudParapetHeight.Value + 400.0, (double)(this._chkAutoDeductBeam.Checked ? 400 : 0));
				string result = "Quick Load Table (Line Load):\n\n";
				foreach (KeyValuePair<int, double> kvp in loadTable)
				{
					result += string.Format("Wall {0}mm: {1:0.00} kN/m\n", kvp.Key, kvp.Value);
				}
				MessageBox.Show(result, "Calculation Complete", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			}
			catch (Exception ex)
			{
				MessageBox.Show("Error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		}

		// Token: 0x0600002A RID: 42 RVA: 0x00003C18 File Offset: 0x00001E18
		private void BtnAssignLoad_Click(object sender, EventArgs e)
		{
			MessageBox.Show("Assign loads to SAP2000 - Coming soon\n\nUsing ILoadBearing interface for polymorphic load assignment.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
		}

		// Token: 0x0600002B RID: 43 RVA: 0x00003C2E File Offset: 0x00001E2E
		public void LoadSettings()
		{
		}

		// Token: 0x0600002C RID: 44 RVA: 0x00003C31 File Offset: 0x00001E31
		public void SaveSettings()
		{
		}

		// Token: 0x0400000C RID: 12
		private GroupBox _grpLoadMethod;

		// Token: 0x0400000D RID: 13
		private RadioButton _rdoAreaLoad;

		// Token: 0x0400000E RID: 14
		private RadioButton _rdoLineLoad;

		// Token: 0x0400000F RID: 15
		private NumericUpDown _nudLoadFactor;

		// Token: 0x04000010 RID: 16
		private CheckBox _chkAutoDeductBeam;

		// Token: 0x04000011 RID: 17
		private ListView _lstStoryLoads;

		// Token: 0x04000012 RID: 18
		private Button _btnMoveUp;

		// Token: 0x04000013 RID: 19
		private Button _btnMoveDown;

		// Token: 0x04000014 RID: 20
		private Button _btnMoveToTop;

		// Token: 0x04000015 RID: 21
		private Button _btnMoveToBottom;

		// Token: 0x04000016 RID: 22
		private Button _btnGetData;

		// Token: 0x04000017 RID: 23
		private Button _btnCalculate;

		// Token: 0x04000018 RID: 24
		private NumericUpDown _nudParapetHeight;

		// Token: 0x04000019 RID: 25
		private NumericUpDown _nudRoofWallHeight;

		// Token: 0x0400001A RID: 26
		private NumericUpDown _nudFireWallFactor;

		// Token: 0x0400001B RID: 27
		private GroupBox _grpModifiers;

		// Token: 0x0400001C RID: 28
		private ListView _lstModifiers;

		// Token: 0x0400001D RID: 29
		private Button _btnFindModifier;

		// Token: 0x0400001E RID: 30
		private Button _btnClearFind;

		// Token: 0x0400001F RID: 31
		private Button _btnAddModifier;

		// Token: 0x04000020 RID: 32
		private Button _btnDelModifier;

		// Token: 0x04000021 RID: 33
		private Button _btnAssignLoad;

		// Token: 0x04000022 RID: 34
		private Button _btnClearModify;

		// Token: 0x04000023 RID: 35
		private Button _btnGetDataBottom;

		// Token: 0x04000024 RID: 36
		private LoadCalculator _calculator;

		// Token: 0x04000025 RID: 37
		private List<AutoLoadTab.StoryLoadItem> _storyLoadItems = new List<AutoLoadTab.StoryLoadItem>();

		// Token: 0x0200005D RID: 93
		private class StoryLoadItem
		{
			// Token: 0x17000156 RID: 342
			// (get) Token: 0x0600048E RID: 1166 RVA: 0x0001E8B2 File Offset: 0x0001CAB2
			// (set) Token: 0x0600048F RID: 1167 RVA: 0x0001E8BA File Offset: 0x0001CABA
			public string Story { get; set; }

			// Token: 0x17000157 RID: 343
			// (get) Token: 0x06000490 RID: 1168 RVA: 0x0001E8C3 File Offset: 0x0001CAC3
			// (set) Token: 0x06000491 RID: 1169 RVA: 0x0001E8CB File Offset: 0x0001CACB
			public string Type { get; set; }

			// Token: 0x17000158 RID: 344
			// (get) Token: 0x06000492 RID: 1170 RVA: 0x0001E8D4 File Offset: 0x0001CAD4
			// (set) Token: 0x06000493 RID: 1171 RVA: 0x0001E8DC File Offset: 0x0001CADC
			public double Thickness { get; set; }

			// Token: 0x17000159 RID: 345
			// (get) Token: 0x06000494 RID: 1172 RVA: 0x0001E8E5 File Offset: 0x0001CAE5
			// (set) Token: 0x06000495 RID: 1173 RVA: 0x0001E8ED File Offset: 0x0001CAED
			public double Height { get; set; }

			// Token: 0x1700015A RID: 346
			// (get) Token: 0x06000496 RID: 1174 RVA: 0x0001E8F6 File Offset: 0x0001CAF6
			// (set) Token: 0x06000497 RID: 1175 RVA: 0x0001E8FE File Offset: 0x0001CAFE
			public double? Load { get; set; }

			// Token: 0x1700015B RID: 347
			// (get) Token: 0x06000498 RID: 1176 RVA: 0x0001E907 File Offset: 0x0001CB07
			// (set) Token: 0x06000499 RID: 1177 RVA: 0x0001E90F File Offset: 0x0001CB0F
			public LoadType LoadType { get; set; } = LoadType.DistributedLine;
		}
	}
}
