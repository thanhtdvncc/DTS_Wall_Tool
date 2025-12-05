using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace DTS_Wall_Tool.UI.Forms
{
	// Token: 0x02000009 RID: 9
	public partial class MainForm : Form
	{
		// Token: 0x06000039 RID: 57 RVA: 0x000049C7 File Offset: 0x00002BC7
		public MainForm()
		{
			this.InitializeComponent();
			this.InitializeUI();
			this.LoadSettings();
		}

		// Token: 0x0600003B RID: 59 RVA: 0x00004A80 File Offset: 0x00002C80
		private void InitializeUI()
		{
			this._mainTabControl = new TabControl
			{
				Dock = DockStyle.Fill,
				Padding = new Point(12, 6)
			};
			TabPage tabWallLineGen = new TabPage("Wall Line Gen");
			this._wallLineGenTab = new WallLineGenTab
			{
				Dock = DockStyle.Fill
			};
			tabWallLineGen.Controls.Add(this._wallLineGenTab);
			TabPage tabLoadAssignment = new TabPage("Load Assignment");
			this._loadAssignmentTab = new LoadAssignmentTab
			{
				Dock = DockStyle.Fill
			};
			tabLoadAssignment.Controls.Add(this._loadAssignmentTab);
			TabPage tabAutoLoad = new TabPage("Auto Load");
			this._autoLoadTab = new AutoLoadTab
			{
				Dock = DockStyle.Fill
			};
			tabAutoLoad.Controls.Add(this._autoLoadTab);
			this._mainTabControl.TabPages.Add(tabWallLineGen);
			this._mainTabControl.TabPages.Add(tabLoadAssignment);
			this._mainTabControl.TabPages.Add(tabAutoLoad);
			this._statusStrip = new StatusStrip();
			this._statusLabel = new ToolStripStatusLabel
			{
				Text = "Ready",
				Spring = true,
				TextAlign = ContentAlignment.MiddleLeft
			};
			this._progressBar = new ToolStripProgressBar
			{
				Visible = false,
				Width = 150
			};
			this._statusStrip.Items.AddRange(new ToolStripItem[] { this._statusLabel, this._progressBar });
			Panel mainPanel = new Panel
			{
				Dock = DockStyle.Fill,
				Padding = new Padding(4)
			};
			mainPanel.Controls.Add(this._mainTabControl);
			base.Controls.Add(mainPanel);
			base.Controls.Add(this._statusStrip);
			base.FormClosing += this.MainForm_FormClosing;
		}

		// Token: 0x0600003C RID: 60 RVA: 0x00004C50 File Offset: 0x00002E50
		public void SetStatus(string message, bool showProgress = false)
		{
			bool invokeRequired = base.InvokeRequired;
			if (invokeRequired)
			{
				base.Invoke(new Action(delegate
				{
					this.SetStatus(message, showProgress);
				}));
			}
			else
			{
				this._statusLabel.Text = message;
				this._progressBar.Visible = showProgress;
			}
		}

		// Token: 0x0600003D RID: 61 RVA: 0x00004CC0 File Offset: 0x00002EC0
		public void SetProgress(int percent)
		{
			bool invokeRequired = base.InvokeRequired;
			if (invokeRequired)
			{
				base.Invoke(new Action(delegate
				{
					this.SetProgress(percent);
				}));
			}
			else
			{
				this._progressBar.Value = Math.Min(100, Math.Max(0, percent));
			}
		}

		// Token: 0x0600003E RID: 62 RVA: 0x00004D24 File Offset: 0x00002F24
		private void LoadSettings()
		{
			try
			{
				this._wallLineGenTab.LoadSettings();
				this._loadAssignmentTab.LoadSettings();
				this._autoLoadTab.LoadSettings();
			}
			catch (Exception ex)
			{
				Debug.WriteLine("Load settings error: " + ex.Message);
			}
		}

		// Token: 0x0600003F RID: 63 RVA: 0x00004D88 File Offset: 0x00002F88
		private void SaveSettings()
		{
			try
			{
				this._wallLineGenTab.SaveSettings();
				this._loadAssignmentTab.SaveSettings();
				this._autoLoadTab.SaveSettings();
			}
			catch (Exception ex)
			{
				Debug.WriteLine("Save settings error: " + ex.Message);
			}
		}

		// Token: 0x06000040 RID: 64 RVA: 0x00004DEC File Offset: 0x00002FEC
		private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
		{
			this.SaveSettings();
		}

		// Token: 0x04000040 RID: 64
		private TabControl _mainTabControl;

		// Token: 0x04000041 RID: 65
		private WallLineGenTab _wallLineGenTab;

		// Token: 0x04000042 RID: 66
		private LoadAssignmentTab _loadAssignmentTab;

		// Token: 0x04000043 RID: 67
		private AutoLoadTab _autoLoadTab;

		// Token: 0x04000044 RID: 68
		private StatusStrip _statusStrip;

		// Token: 0x04000045 RID: 69
		private ToolStripStatusLabel _statusLabel;

		// Token: 0x04000046 RID: 70
		private ToolStripProgressBar _progressBar;
	}
}
