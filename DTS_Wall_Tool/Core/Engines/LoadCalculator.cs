using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Interfaces;
using DTS_Wall_Tool.Core.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Wall_Tool.Core.Engines
{
    /// <summary>
    /// Tính toán tải trọng tường và các phần tử khác.
    /// Hỗ trợ ILoadBearing interface cho đa hình.
    /// 
    /// ⚠️ CLEAN ARCHITECTURE (v2.1+):
    /// - Sử dụng UnitManager để xử lý đơn vị
    /// - Tải trọng được lưu vào Loads list (ILoadBearing)
    /// - Không còn gán trực tiếp LoadValue, LoadPattern
    /// </summary>
    public class LoadCalculator
    {
    #region Configuration

        /// <summary>
        /// Dung trọng tường xây (kN/m³)
        /// ⚠️ ĐƠN VỊ CỐ ĐỊNH: luôn là kN/m³
        /// </summary>
        public double WallUnitWeight { get; set; } = 18.0;

    /// <summary>
     /// Chiều cao tầng mặc định (theo đơn vị CAD - thường là mm)
        /// </summary>
        public double DefaultStoryHeight { get; set; } = 3300;

        /// <summary>
  /// Chiều cao dầm trừ đi (theo đơn vị CAD - thường là mm)
        /// </summary>
        public double BeamHeightDeduction { get; set; } = 400;

  /// <summary>
        /// Chiều dày vữa trát mỗi bên (theo đơn vị CAD - thường là mm)
        /// </summary>
        public double PlasterThickness { get; set; } = 15;

        /// <summary>
   /// Dung trọng vữa trát (kN/m³)
        /// </summary>
        public double PlasterUnitWeight { get; set; } = 20.0;

        /// <summary>
        /// Hệ số tải trọng
/// </summary>
   public double LoadFactor { get; set; } = 1.0;

        /// <summary>
        /// Load pattern mặc định
        /// </summary>
        public string DefaultLoadPattern { get; set; } = "DL";

        /// <summary>
    /// Danh sách modifier
        /// </summary>
     public List<LoadModifier> Modifiers { get; set; } = new List<LoadModifier>();

      #endregion

   #region Constructors

public LoadCalculator()
        {
            InitializeDefaultModifiers();
        }

        public LoadCalculator(double storyHeight, double beamHeight) : this()
        {
     DefaultStoryHeight = storyHeight;
      BeamHeightDeduction = beamHeight;
        }

        #endregion

        #region Main Calculation Methods

     /// <summary>
      /// Tính tải phân bố (kN/m).
        /// 
        /// ⚠️ XỬ LÝ ĐƠN VỊ:
        /// - thickness và height theo đơn vị CAD (thường mm)
        /// - Tự động quy đổi về Mét thông qua UnitManager
        /// - Kết quả luôn là kN/m
    /// </summary>
        /// <param name="thickness">Độ dày tường (theo đơn vị CAD)</param>
        /// <param name="height">Chiều cao tường (theo đơn vị CAD)</param>
        /// <param name="modifierNames">Danh sách modifier áp dụng</param>
        public double CalculateLineLoad(double thickness, double height, IEnumerable<string> modifierNames = null)
      {
     // Lấy hệ số quy đổi từ UnitManager
   double scaleToMeter = UnitManager.Info.LengthScaleToMeter;

            // Chuyển đổi về Mét
    double thickM = thickness * scaleToMeter;
            double heightM = height * scaleToMeter;
       double plasterM = PlasterThickness * scaleToMeter;

        // Tải tường cơ bản (kN/m²)
double wallLoadPerM2 = thickM * WallUnitWeight;

     // Tải vữa trát (2 mặt)
   double plasterLoadPerM2 = plasterM * PlasterUnitWeight * 2;

            // Tổng tải diện tích
            double totalAreaLoad = wallLoadPerM2 + plasterLoadPerM2;

       // Chuyển sang tải đường (kN/m)
            double lineLoad = totalAreaLoad * heightM;

         // Áp dụng modifiers
         if (modifierNames != null)
   {
   foreach (var modName in modifierNames)
    {
        var mod = Modifiers.FirstOrDefault(m =>
       m.Name.Equals(modName, StringComparison.OrdinalIgnoreCase));

  if (mod != null)
     {
      lineLoad = ApplyModifier(lineLoad, mod, height);
  }
             }
}

  // Áp dụng hệ số tải
            lineLoad *= LoadFactor;

       return Math.Round(lineLoad, 2);
  }

   /// <summary>
     /// Tính tải với chiều cao mặc định (có trừ dầm)
  /// </summary>
        public double CalculateLineLoadWithDeduction(double thickness, IEnumerable<string> modifierNames = null)
      {
      double effectiveHeight = DefaultStoryHeight - BeamHeightDeduction;
            return CalculateLineLoad(thickness, effectiveHeight, modifierNames);
     }

    /// <summary>
        /// Tính và gán tải cho WallData.
        /// 
        /// ⚠️ CLEAN ARCHITECTURE:
        /// - Cập nhật Height và UnitWeight vào WallData
        /// - Gọi CalculateLoads() để tạo LoadDefinition
        /// - Không gán trực tiếp LoadValue, LoadPattern
        /// </summary>
        public void CalculateAndAssign(WallData wallData, double storyHeight = 0)
        {
            if (!wallData.Thickness.HasValue || wallData.Thickness.Value <= 0)
         return;

        double height = storyHeight > 0 ? storyHeight : DefaultStoryHeight;
            double effectiveHeight = height - BeamHeightDeduction;

     // Cập nhật thông số để WallData.CalculateLoads() sử dụng
         wallData.Height = effectiveHeight;
            
        if (!wallData.UnitWeight.HasValue)
       wallData.UnitWeight = WallUnitWeight;
            
wallData.LoadFactor = LoadFactor;

   // Gọi CalculateLoads() - tải sẽ được thêm vào Loads list
    wallData.CalculateLoads();
      }

 /// <summary>
        /// Tính và gán tải cho phần tử ILoadBearing (đa hình).
     /// 
  /// ⚠️ WORKFLOW:
  /// 1. Chuẩn bị thông số (Height, UnitWeight, LoadFactor)
/// 2. Gọi CalculateLoads() của từng phần tử
        /// 3. Tải được lưu vào Loads list
        /// </summary>
        public void CalculateAndAssign(ILoadBearing loadBearing, double storyHeight = 0)
        {
            // Phân luồng xử lý theo loại phần tử
            if (loadBearing is WallData wallData)
    {
           CalculateAndAssign(wallData, storyHeight);
   }
      else if (loadBearing is SlabData slabData)
            {
                CalculateSlabLoad(slabData);
            }
            else if (loadBearing is BeamData beamData)
         {
                CalculateBeamLoad(beamData);
         }
     // Thêm các loại phần tử khác ở đây...
        }

        /// <summary>
    /// Tính tải sàn (kN/m²)
        /// </summary>
  private void CalculateSlabLoad(SlabData slabData)
    {
     // Gọi CalculateLoads() của SlabData nếu có
            slabData.CalculateLoads();
        }

        /// <summary>
        /// Tính tải dầm (kN/m)
        /// </summary>
        private void CalculateBeamLoad(BeamData beamData)
      {
    // Gọi CalculateLoads() của BeamData nếu có
            beamData.CalculateLoads();
        }

        #endregion

        #region Helper Methods

   private double ApplyModifier(double baseLoad, LoadModifier mod, double height)
        {
            switch (mod.Type.ToUpperInvariant())
   {
           case "FACTOR":
               return baseLoad * mod.Factor;

        case "HEIGHT_OVERRIDE":
     // HeightOverride cũng theo đơn vị CAD
           double scaleToMeter = UnitManager.Info.LengthScaleToMeter;
            double overrideHeightM = mod.HeightOverride * scaleToMeter;
    double currentHeightM = height * scaleToMeter;
         
                    if (currentHeightM > 0)
        return baseLoad * (overrideHeightM / currentHeightM);
             return baseLoad;

    case "ADD":
         return baseLoad + mod.AddValue;

        case "SUBTRACT":
      return baseLoad - mod.AddValue;

       default:
        return baseLoad;
       }
        }

   private List<string> ExtractModifiersFromType(string wallType)
        {
            var modifiers = new List<string>();

    if (string.IsNullOrEmpty(wallType))
       return modifiers;

  var parts = wallType.Split('_');
   foreach (var part in parts.Skip(1))
          {
    if (Modifiers.Any(m => m.Name.Equals(part, StringComparison.OrdinalIgnoreCase)))
    {
     modifiers.Add(part);
        }
   }

       return modifiers;
        }

     #endregion

      #region Initialization

        private void InitializeDefaultModifiers()
        {
            Modifiers = new List<LoadModifier>
   {
                new LoadModifier
    {
        Name = "PARAPET",
    Type = "HEIGHT_OVERRIDE",
      HeightOverride = 1200,
     Description = "Tường lan can (cao 1.2m)"
         },
           new LoadModifier
       {
  Name = "HALF",
        Type = "FACTOR",
         Factor = 0.5,
             Description = "Tường nửa chiều cao"
 },
    new LoadModifier
  {
      Name = "FIRE",
     Type = "ADD",
      AddValue = 0.5,
           Description = "Tường chống cháy (+0.5 kN/m)"
      },
                new LoadModifier
    {
         Name = "FULL",
    Type = "HEIGHT_OVERRIDE",
HeightOverride = DefaultStoryHeight,
    Description = "Tường full (không trừ dầm)"
                }
            };
     }

 #endregion

        #region Static Helpers

  /// <summary>
     /// Bảng tra nhanh tải cho các độ dày phổ biến
        /// </summary>
   public static Dictionary<int, double> GetQuickLoadTable(double storyHeight = 3300, double beamHeight = 400)
        {
   var calc = new LoadCalculator(storyHeight, beamHeight);

      int[] thicknesses = { 100, 110, 150, 200, 220, 250, 300 };
      var result = new Dictionary<int, double>();

      foreach (var t in thicknesses)
     {
    result[t] = calc.CalculateLineLoadWithDeduction(t);
            }

            return result;
   }

        #endregion
    }

    /// <summary>
  /// Định nghĩa modifier cho tải trọng
    /// </summary>
    public class LoadModifier
    {
        public string Name { get; set; }
        public string Type { get; set; } // FACTOR, HEIGHT_OVERRIDE, ADD, SUBTRACT
   public double Factor { get; set; } = 1.0;
        public double HeightOverride { get; set; }
     public double AddValue { get; set; }
  public string Description { get; set; }

        public override string ToString() => $"{Name} ({Type})";
    }
}