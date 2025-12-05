using DTS_Wall_Tool.Core.Interfaces;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Wall_Tool.Core.Data
{
    /// <summary>
    /// Dữ liệu Dầm - Kế thừa từ ElementData và implement ILoadBearing.
    /// </summary>
    public class BeamData : ElementData, ILoadBearing
    {
      #region Identity Override

      public override ElementType ElementType => ElementType.Beam;

        #endregion

 #region Beam-Specific Properties

        /// <summary>
        /// Chiều rộng dầm (mm)
        /// </summary>
        public double? Width { get; set; } = null;

        /// <summary>
        /// Chiều cao dầm (mm)
        /// </summary>
        public double? Depth { get; set; } = null;

        /// <summary>
        /// Alias cho Depth (chiều cao dầm) để tương thích với LabelUtils
        /// </summary>
        public new double? Height
        {
  get => Depth;
            set => Depth = value;
        }

        /// <summary>
        /// Tên tiết diện (VD: "B200x400", "B300x600")
        /// </summary>
 public string SectionName { get; set; } = null;

    /// <summary>
        /// Chiều dài dầm (mm)
   /// </summary>
      public double? Length { get; set; } = null;

        /// <summary>
        /// Vật liệu
        /// </summary>
        public string Material { get; set; } = "Concrete";

      /// <summary>
        /// Mác bê tông
     /// </summary>
        public string ConcreteGrade { get; set; } = "C30";

        /// <summary>
        /// Loại dầm (Main, Secondary, Cantilever...)
        /// </summary>
        public string BeamType { get; set; } = "Main";

        /// <summary>
        /// Dung trọng bê tông (kN/m³)
        /// </summary>
        public double UnitWeight { get; set; } = 25.0;

        /// <summary>
      /// Load pattern mặc định
        /// </summary>
        public string LoadPattern { get; set; } = "DL";

        #endregion

        #region ILoadBearing Implementation

        /// <summary>
        /// Danh sách tải trọng chuẩn theo Interface ILoadBearing
   /// </summary>
     public List<LoadDefinition> Loads { get; set; } = new List<LoadDefinition>();

        /// <summary>
     /// Kiểm tra có tải trọng để gán không
        /// </summary>
        public bool HasLoads => Loads != null && Loads.Count > 0;

        /// <summary>
        /// Xóa tất cả tải trọng đã tính
   /// </summary>
        public void ClearLoads()
        {
  Loads?.Clear();
        }

        /// <summary>
        /// Tính toán tải trọng bản thân dầm và đưa vào danh sách Loads.
     /// Tải dầm = Width(m) * Depth(m) * UnitWeight(kN/m³) = kN/m
        /// </summary>
    public void CalculateLoads()
        {
            ClearLoads();

            if (!Width.HasValue || !Depth.HasValue) return;

            // Tính tải bản thân dầm (kN/m)
  double widthM = Width.Value / 1000.0;
  double depthM = Depth.Value / 1000.0;
       double selfWeight = widthM * depthM * UnitWeight;
      selfWeight = System.Math.Round(selfWeight, 2);

  // Thêm tải bản thân
       Loads.Add(new LoadDefinition
            {
         Pattern = LoadPattern ?? "DL",
       Value = selfWeight,
         Type = LoadType.DistributedLine,
      TargetElement = "Frame",
       Direction = "Gravity"
            });
      }

     #endregion

        #region Override Methods

        public override bool HasValidData()
        {
    return (Width.HasValue && Depth.HasValue) || !string.IsNullOrEmpty(SectionName);
  }

        public override ElementData Clone()
        {
   var clone = new BeamData
     {
       Width = Width,
       Depth = Depth,
          SectionName = SectionName,
         Length = Length,
  Material = Material,
          ConcreteGrade = ConcreteGrade,
 BeamType = BeamType,
 UnitWeight = UnitWeight,
   LoadPattern = LoadPattern
          };

    // Clone Loads (ILoadBearing)
            if (Loads != null)
          {
    clone.Loads = Loads.Select(l => l.Clone()).ToList();
         }

            CopyBaseTo(clone);
        return clone;
        }

        public override Dictionary<string, object> ToDictionary()
        {
  var dict = new Dictionary<string, object>();
       WriteBaseProperties(dict);

            if (Width.HasValue) dict["xWidth"] = Width.Value;
   if (Depth.HasValue) dict["xDepth"] = Depth.Value;
 if (!string.IsNullOrEmpty(SectionName)) dict["xSectionName"] = SectionName;
  if (Length.HasValue) dict["xLength"] = Length.Value;
            if (!string.IsNullOrEmpty(Material)) dict["xMaterial"] = Material;
            if (!string.IsNullOrEmpty(ConcreteGrade)) dict["xConcreteGrade"] = ConcreteGrade;
            if (!string.IsNullOrEmpty(BeamType)) dict["xBeamType"] = BeamType;
  dict["xUnitWeight"] = UnitWeight;
 if (!string.IsNullOrEmpty(LoadPattern)) dict["xLoadPattern"] = LoadPattern;

     // Serialize Loads (ILoadBearing)
     if (Loads != null && Loads.Count > 0)
            {
     var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
var loadsList = Loads.Select(l => new Dictionary<string, object>
                {
     ["Pattern"] = l.Pattern,
    ["Value"] = l.Value,
       ["Type"] = l.Type.ToString(),
         ["TargetElement"] = l.TargetElement,
             ["Direction"] = l.Direction,
   ["LoadFactor"] = l.LoadFactor
             }).ToList();
    dict["xLoads"] = serializer.Serialize(loadsList);
   }

   return dict;
        }

   public override void FromDictionary(Dictionary<string, object> dict)
        {
         ReadBaseProperties(dict);

  if (dict.TryGetValue("xWidth", out var w)) Width = ConvertToDouble(w);
  if (dict.TryGetValue("xDepth", out var d)) Depth = ConvertToDouble(d);
         if (dict.TryGetValue("xSectionName", out var sn)) SectionName = sn?.ToString();
     if (dict.TryGetValue("xLength", out var len)) Length = ConvertToDouble(len);
    if (dict.TryGetValue("xMaterial", out var mat)) Material = mat?.ToString();
      if (dict.TryGetValue("xConcreteGrade", out var cg)) ConcreteGrade = cg?.ToString();
       if (dict.TryGetValue("xBeamType", out var bt)) BeamType = bt?.ToString();
     if (dict.TryGetValue("xUnitWeight", out var uw)) UnitWeight = ConvertToDouble(uw) ?? 25.0;
     if (dict.TryGetValue("xLoadPattern", out var lp)) LoadPattern = lp?.ToString();

      // Deserialize Loads (ILoadBearing)
       if (dict.TryGetValue("xLoads", out var loadsJson))
         {
 try
                {
  var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
           var loadsList = serializer.Deserialize<List<Dictionary<string, object>>>(loadsJson.ToString());
Loads = new List<LoadDefinition>();

               foreach (var loadDict in loadsList)
     {
            var load = new LoadDefinition();
       if (loadDict.TryGetValue("Pattern", out var p)) load.Pattern = p?.ToString();
      if (loadDict.TryGetValue("Value", out var v)) load.Value = System.Convert.ToDouble(v);
       if (loadDict.TryGetValue("Type", out var t))
      {
            if (System.Enum.TryParse<LoadType>(t.ToString(), out var lt))
             load.Type = lt;
        }
 if (loadDict.TryGetValue("TargetElement", out var te)) load.TargetElement = te?.ToString();
if (loadDict.TryGetValue("Direction", out var dir)) load.Direction = dir?.ToString();
             if (loadDict.TryGetValue("LoadFactor", out var lf)) load.LoadFactor = System.Convert.ToDouble(lf);
    Loads.Add(load);
       }
          }
       catch
      {
      Loads = new List<LoadDefinition>();
        }
            }
        }

        #endregion

        #region Beam-Specific Methods

        public void EnsureSectionName()
  {
   if (string.IsNullOrEmpty(SectionName) && Width.HasValue && Depth.HasValue)
            {
    SectionName = $"B{(int)Width.Value}x{(int)Depth.Value}";
            }
        }

        /// <summary>
        /// Tính tải bản thân dầm (kN/m)
        /// </summary>
   public double GetSelfWeight()
     {
        if (!Width.HasValue || !Depth.HasValue) return 0;
            double widthM = Width.Value / 1000.0;
      double depthM = Depth.Value / 1000.0;
          return System.Math.Round(widthM * depthM * UnitWeight, 2);
        }

        public override string ToString()
        {
         string sizeStr = SectionName ?? $"{Width ?? 0}x{Depth ?? 0}";
  string loadStr = HasLoads ? $"{Loads.Sum(l => l.Value):0.00}kN/m" : "[N/A]";
            string linkStatus = IsLinked ? "Linked" : "Unlinked";
    return $"Beam[{sizeStr}] {BeamType}, Load={loadStr}, {linkStatus}";
        }

        #endregion
    }
}