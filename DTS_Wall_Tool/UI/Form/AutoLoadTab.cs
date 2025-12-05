using DTS_Wall_Tool.Core.Engines;
using DTS_Wall_Tool.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DTS_Wall_Tool.UI.Forms
{
    /// <summary>
    /// Tab tự động tính tải
    /// </summary>
    public class AutoLoadTab : UserControl
    {
     #region Controls

 // Load Calculation Method
   private GroupBox _grpLoadMethod;
      private RadioButton _rdoAreaLoad;
        private RadioButton _rdoLineLoad;
        private NumericUpDown _nudLoadFactor;
      private CheckBox _chkAutoDeductBeam;

 // Story/Load Grid
    private ListView _lstStoryLoads;
     private Button _btnMoveUp;
private Button _btnMoveDown;
   private Button _btnMoveToTop;
  private Button _btnMoveToBottom;

   // Actions
    private Button _btnGetData;
        private Button _btnCalculate;

        // Parameters
     private NumericUpDown _nudParapetHeight;
   private NumericUpDown _nudRoofWallHeight;
        private NumericUpDown _nudFireWallFactor;

        // Modifiers
     private GroupBox _grpModifiers;
 private ListView _lstModifiers;
        private Button _btnFindModifier;
        private Button _btnClearFind;
     private Button _btnAddModifier;
   private Button _btnDelModifier;

   // Bottom Buttons
        private Button _btnAssignLoad;
        private Button _btnClearModify;
        private Button _btnGetDataBottom;

        #endregion

  #region Data

private LoadCalculator _calculator;
    private List<StoryLoadItem> _storyLoadItems = new List<StoryLoadItem>();

    #endregion

   #region Constructors

        public AutoLoadTab()
        {
            _calculator = new LoadCalculator();
    InitializeUI();
 }

        public AutoLoadTab(LoadCalculator calculator)
        {
_calculator = calculator ?? new LoadCalculator();
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
        ColumnCount = 1,
   RowCount = 5
         };
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
    mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 40F));
       mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
  mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 30F));
      mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

          // Row 0: Load Calculation Method
            _grpLoadMethod = new GroupBox
        {
     Text = "Load Calculation Method:",
    Dock = DockStyle.Fill
            };

            var methodLayout = new TableLayoutPanel
   {
      Dock = DockStyle.Fill,
        ColumnCount = 4,
    RowCount = 3,
       Padding = new Padding(5)
   };

         _rdoAreaLoad = new RadioButton
 {
          Text = "Area Load (kN/m²) - For Slab/Shell",
         AutoSize = true
   };
_rdoLineLoad = new RadioButton
 {
    Text = "Line Load (kN/m) - For Wall/Beam",
   AutoSize = true,
       Checked = true
       };

    methodLayout.Controls.Add(_rdoAreaLoad, 0, 0);
            methodLayout.SetColumnSpan(_rdoAreaLoad, 4);
     methodLayout.Controls.Add(_rdoLineLoad, 0, 1);
            methodLayout.SetColumnSpan(_rdoLineLoad, 4);

     // Load Factor row
    var factorPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
factorPanel.Controls.Add(new Label { Text = "Load Factor:", AutoSize = true, Margin = new Padding(0, 6, 5, 0) });
     _nudLoadFactor = new NumericUpDown
 {
     Width = 60,
     Minimum = 0.1M,
          Maximum = 10M,
     DecimalPlaces = 2,
     Value = 1M,
Increment = 0.1M
      };
    factorPanel.Controls.Add(_nudLoadFactor);
       factorPanel.Controls.Add(new Label { Text = "(Global multiplier)", AutoSize = true, Margin = new Padding(10, 6, 0, 0), ForeColor = Color.Gray });

            var factorBtnPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        factorBtnPanel.Controls.Add(new Button { Text = "[ + ]", Width = 40, Height = 25 });
factorBtnPanel.Controls.Add(new Button { Text = "[ - ]", Width = 40, Height = 25 });

          methodLayout.Controls.Add(factorPanel, 0, 2);
       methodLayout.SetColumnSpan(factorPanel, 2);
     methodLayout.Controls.Add(factorBtnPanel, 3, 2);

      _chkAutoDeductBeam = new CheckBox
     {
    Text = "Auto deduct beam depth from height",
       AutoSize = true
    };
       methodLayout.Controls.Add(_chkAutoDeductBeam, 2, 2);

      _grpLoadMethod.Controls.Add(methodLayout);
            mainLayout.Controls.Add(_grpLoadMethod, 0, 0);

     // Row 1: Story/Load Grid
     var gridPanel = new Panel { Dock = DockStyle.Fill };

     _lstStoryLoads = new ListView
            {
      Dock = DockStyle.Fill,
 View = View.Details,
      FullRowSelect = true,
      GridLines = true,
     Font = new Font("Consolas", 9F)
     };
   _lstStoryLoads.Columns.Add("Story", 60);
_lstStoryLoads.Columns.Add("Type", 50);
     _lstStoryLoads.Columns.Add("Thick", 50);
         _lstStoryLoads.Columns.Add("Height", 70);
     _lstStoryLoads.Columns.Add("Load", 70);

   // Sample data
   AddSampleStoryData();

            var gridBtnPanel = new FlowLayoutPanel
 {
   Dock = DockStyle.Right,
   Width = 50,
      FlowDirection = FlowDirection.TopDown,
          WrapContents = false,
Padding = new Padding(5)
     };

    _btnMoveToTop = new Button { Text = ">>", Width = 35, Height = 25 };
   _btnMoveUp = new Button { Text = "=>", Width = 35, Height = 25 };
_btnMoveDown = new Button { Text = "<=", Width = 35, Height = 25 };
 _btnMoveToBottom = new Button { Text = "<<", Width = 35, Height = 25 };

     gridBtnPanel.Controls.Add(_btnMoveToTop);
     gridBtnPanel.Controls.Add(_btnMoveUp);
            gridBtnPanel.Controls.Add(_btnMoveDown);
         gridBtnPanel.Controls.Add(_btnMoveToBottom);

            gridPanel.Controls.Add(_lstStoryLoads);
    gridPanel.Controls.Add(gridBtnPanel);

     // Get Data and Calculate buttons
   var actionPanel = new FlowLayoutPanel
            {
       Dock = DockStyle.Left,
     Width = 80,
    FlowDirection = FlowDirection.TopDown,
    WrapContents = false,
       Padding = new Padding(0, 5, 5, 5)
         };

     _btnGetData = new Button { Text = "Get Data", Width = 70, Height = 28, Margin = new Padding(0, 2, 0, 2) };
    _btnCalculate = new Button
       {
   Text = "Calculate",
      Width = 70,
    Height = 28,
            BackColor = Color.FromArgb(0, 150, 136),
ForeColor = Color.White,
    FlatStyle = FlatStyle.Flat,
     Margin = new Padding(0, 2, 0, 2)
   };

            _btnGetData.Click += BtnGetData_Click;
          _btnCalculate.Click += BtnCalculate_Click;

    actionPanel.Controls.Add(_btnGetData);
            actionPanel.Controls.Add(_btnCalculate);

         gridPanel.Controls.Add(actionPanel);

    mainLayout.Controls.Add(gridPanel, 0, 1);

  // Row 2: Parameters
            var paramPanel = new TableLayoutPanel
          {
       Dock = DockStyle.Fill,
  ColumnCount = 4,
                RowCount = 3
            };

            paramPanel.Controls.Add(new Label { Text = "Parapet Height:", AutoSize = true }, 0, 0);
         _nudParapetHeight = new NumericUpDown { Width = 70, Value = 1200, Maximum = 5000 };
   paramPanel.Controls.Add(_nudParapetHeight, 1, 0);
 paramPanel.Controls.Add(new Label { Text = "mm", AutoSize = true }, 2, 0);
            paramPanel.Controls.Add(new Button { Text = ">>", Width = 35 }, 3, 0);

            paramPanel.Controls.Add(new Label { Text = "Roof Wall Height:", AutoSize = true }, 0, 1);
  _nudRoofWallHeight = new NumericUpDown { Width = 70, Value = 1500, Maximum = 5000 };
         paramPanel.Controls.Add(_nudRoofWallHeight, 1, 1);
            paramPanel.Controls.Add(new Label { Text = "mm", AutoSize = true }, 2, 1);

            paramPanel.Controls.Add(new Label { Text = "Fire Wall Factor:", AutoSize = true }, 0, 2);
         _nudFireWallFactor = new NumericUpDown { Width = 70, Value = 1.2M, DecimalPlaces = 2, Increment = 0.1M, Minimum = 0.1M, Maximum = 5M };
 paramPanel.Controls.Add(_nudFireWallFactor, 1, 2);

    mainLayout.Controls.Add(paramPanel, 0, 2);

        // Row 3: Load Modifiers
            _grpModifiers = new GroupBox
  {
 Text = "Load Modifiers:",
   Dock = DockStyle.Fill
   };

   var modLayout = new TableLayoutPanel
        {
   Dock = DockStyle.Fill,
      ColumnCount = 2,
 RowCount = 1,
    Padding = new Padding(5)
     };
            modLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            modLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            var modBtnPanel = new FlowLayoutPanel
          {
           Dock = DockStyle.Fill,
     FlowDirection = FlowDirection.TopDown,
           WrapContents = false
        };

            _btnFindModifier = new Button { Text = "Find", Width = 70, Height = 25, Margin = new Padding(2) };
  _btnClearFind = new Button { Text = "Clear Find", Width = 70, Height = 25, Margin = new Padding(2) };

      modBtnPanel.Controls.Add(_btnFindModifier);
    modBtnPanel.Controls.Add(_btnClearFind);

    modLayout.Controls.Add(modBtnPanel, 0, 0);

            var modListPanel = new Panel { Dock = DockStyle.Fill };

  _lstModifiers = new ListView
    {
     Dock = DockStyle.Fill,
      View = View.Details,
         FullRowSelect = true,
    GridLines = true,
      Font = new Font("Consolas", 9F)
    };
      _lstModifiers.Columns.Add("Name", 100);
        _lstModifiers.Columns.Add("Value", 60);
      _lstModifiers.Columns.Add("Opera", 50);
            _lstModifiers.Columns.Add("Ov", 30);

            // Add default modifiers from calculator
 foreach (var mod in _calculator.Modifiers)
  {
    var modItem = new ListViewItem(mod.Name);
   modItem.SubItems.Add(mod.Type == "HEIGHT_OVERRIDE" ? mod.HeightOverride.ToString() :
         mod.Type == "FACTOR" ? mod.Factor.ToString() : mod.AddValue.ToString());
     modItem.SubItems.Add(mod.Type);
     modItem.SubItems.Add("-");
    _lstModifiers.Items.Add(modItem);
         }

    var modRightBtnPanel = new FlowLayoutPanel
  {
 Dock = DockStyle.Right,
      Width = 50,
     FlowDirection = FlowDirection.TopDown,
        WrapContents = false
  };

  _btnAddModifier = new Button { Text = "Add", Width = 40, Height = 25, Margin = new Padding(2) };
          _btnDelModifier = new Button { Text = "Del", Width = 40, Height = 25, Margin = new Padding(2) };

      modRightBtnPanel.Controls.Add(_btnAddModifier);
      modRightBtnPanel.Controls.Add(_btnDelModifier);

       modListPanel.Controls.Add(_lstModifiers);
 modListPanel.Controls.Add(modRightBtnPanel);

            modLayout.Controls.Add(modListPanel, 1, 0);

          _grpModifiers.Controls.Add(modLayout);
      mainLayout.Controls.Add(_grpModifiers, 0, 3);

            // Row 4: Bottom Buttons
var bottomPanel = new FlowLayoutPanel
     {
              Dock = DockStyle.Fill,
    WrapContents = false
            };

         _btnAssignLoad = new Button
     {
                Text = "Assign Load",
   Width = 95,
 Height = 28,
     BackColor = Color.FromArgb(0, 122, 204),
      ForeColor = Color.White,
    FlatStyle = FlatStyle.Flat
            };
            _btnClearModify = new Button { Text = "Clear all Modify", Width = 100, Height = 28 };
     _btnGetDataBottom = new Button
            {
              Text = "Get Data",
       Width = 80,
    Height = 28,
         BackColor = Color.FromArgb(76, 175, 80),
  ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat
    };

       _btnAssignLoad.Click += BtnAssignLoad_Click;

  bottomPanel.Controls.Add(_btnAssignLoad);
      bottomPanel.Controls.Add(_btnClearModify);
     bottomPanel.Controls.Add(_btnGetDataBottom);

   mainLayout.Controls.Add(bottomPanel, 0, 4);

     this.Controls.Add(mainLayout);
    }

        private void AddSampleStoryData()
        {
            var data = new[]
   {
     new { Story = "500", Type = "W200", Thick = "200", Height = "6500.00", Load = "" },
        new { Story = "7000", Type = "W200", Thick = "200", Height = "4500.00", Load = "" },
                new { Story = "11500", Type = "W200", Thick = "200", Height = "3600.00", Load = "" },
         new { Story = "15100", Type = "W200", Thick = "200", Height = "0.00", Load = "" }
 };

            foreach (var d in data)
  {
            var item = new ListViewItem(d.Story);
                item.SubItems.Add(d.Type);
         item.SubItems.Add(d.Thick);
            item.SubItems.Add(d.Height);
       item.SubItems.Add(d.Load);
    _lstStoryLoads.Items.Add(item);
          }
        }

 #endregion

        #region Event Handlers

    private void BtnGetData_Click(object sender, EventArgs e)
        {
            // TODO: Get data from SAP2000
       MessageBox.Show("Get data from SAP2000 - Coming soon", "Info",
           MessageBoxButtons.OK, MessageBoxIcon.Information);
      }

    private void BtnCalculate_Click(object sender, EventArgs e)
        {
            try
            {
       // Update calculator settings
_calculator.LoadFactor = (double)_nudLoadFactor.Value;
         
   if (_chkAutoDeductBeam.Checked)
          {
      _calculator.BeamHeightDeduction = 400;
         }
     else
        {
   _calculator.BeamHeightDeduction = 0;
           }

          // Calculate for each item in list
    foreach (ListViewItem item in _lstStoryLoads.Items)
 {
    if (double.TryParse(item.SubItems[2].Text, out double thickness) &&
       double.TryParse(item.SubItems[3].Text, out double height))
                    {
          // Determine load type based on radio button
 if (_rdoLineLoad.Checked)
       {
  // Line load (kN/m) for walls
        double load = _calculator.CalculateLineLoad(thickness, height);
    item.SubItems[4].Text = $"{load:0.00}";
             }
         else
       {
          // Area load (kN/m²) for slabs - just thickness * unit weight
            double thickM = thickness / 1000.0;
            double areaLoad = thickM * _calculator.WallUnitWeight * _calculator.LoadFactor;
 item.SubItems[4].Text = $"{areaLoad:0.00}";
      }
 }
           }

          // Also show quick table
           var loadTable = LoadCalculator.GetQuickLoadTable(
        (double)_nudParapetHeight.Value + 400,  // Example story height
            _chkAutoDeductBeam.Checked ? 400 : 0
                );

            string result = "Quick Load Table (Line Load):\n\n";
        foreach (var kvp in loadTable)
   {
    result += $"Wall {kvp.Key}mm: {kvp.Value:0.00} kN/m\n";
           }

        MessageBox.Show(result, "Calculation Complete",
      MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
 catch (Exception ex)
{
MessageBox.Show($"Error: {ex.Message}", "Error",
          MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
     }

        private void BtnAssignLoad_Click(object sender, EventArgs e)
        {
            MessageBox.Show("Assign loads to SAP2000 - Coming soon\n\nUsing ILoadBearing interface for polymorphic load assignment.", "Info",
      MessageBoxButtons.OK, MessageBoxIcon.Information);
   }

        #endregion

        #region Settings

        public void LoadSettings() { }
        public void SaveSettings() { }

      #endregion

      #region Helper Classes

        private class StoryLoadItem
        {
  public string Story { get; set; }
            public string Type { get; set; }
   public double Thickness { get; set; }
            public double Height { get; set; }
   public double? Load { get; set; }
            public LoadType LoadType { get; set; } = LoadType.DistributedLine;
        }

 #endregion
    }
}