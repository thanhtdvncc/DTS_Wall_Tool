using DTS_Wall_Tool.Core.Data;
using System.Collections.Generic;

namespace DTS_Wall_Tool.Core.Interfaces
{
    /// <summary>
    /// ??nh ngh?a lo?i t?i tr?ng ?? Engine x? lý ?úng ph??ng th?c gán t?i
    /// </summary>
public enum LoadType
    {
/// <summary>
        /// T?i phân b? lên thanh (kN/m) -> Gán cho Frame (D?m/C?t)
        /// </summary>
      DistributedLine,

     /// <summary>
        /// T?i phân b? ??u lên di?n tích (kN/m²) -> Gán cho Area (Sàn/Vách)
        /// </summary>
  UniformArea,

 /// <summary>
        /// T?i t?p trung (kN) -> Gán cho Point ho?c Frame
        /// </summary>
        Point
 }

    /// <summary>
    /// C?u trúc l?u tr? m?t m?c t?i tr?ng ??n l?.
    /// Cho phép m?t ph?n t? CAD mang nhi?u lo?i t?i khác nhau.
    /// </summary>
    public class LoadDefinition
    {
/// <summary>
        /// Load Pattern trong SAP2000 (VD: "DL", "SDL", "WIND")
        /// </summary>
        public string Pattern { get; set; } = "DL";

        /// <summary>
        /// Giá tr? t?i tr?ng (??n v? tùy thu?c LoadType)
        /// - DistributedLine: kN/m
  /// - UniformArea: kN/m²
        /// - Point: kN
        /// </summary>
        public double Value { get; set; }

    /// <summary>
        /// Lo?i t?i tr?ng - quy?t ??nh ph??ng th?c gán t?i trong SAP
        /// </summary>
  public LoadType Type { get; set; } = LoadType.DistributedLine;

        /// <summary>
        /// Lo?i ??i t??ng ?ích trong SAP ("Frame" ho?c "Area")
        /// </summary>
        public string TargetElement { get; set; } = "Frame";

        /// <summary>
    /// H??ng t?i (Gravity, X, Y, Z)
        /// </summary>
        public string Direction { get; set; } = "Gravity";

      /// <summary>
        /// V? trí b?t ??u t?i trên ph?n t? (mm ho?c t? l? 0-1)
        /// </summary>
        public double DistI { get; set; } = 0;

        /// <summary>
        /// V? trí k?t thúc t?i trên ph?n t? (mm ho?c t? l? 0-1)
   /// </summary>
        public double DistJ { get; set; } = 0;

 /// <summary>
        /// S? d?ng kho?ng cách t??ng ??i (true) hay tuy?t ??i (false)
        /// </summary>
        public bool IsRelativeDistance { get; set; } = false;

        /// <summary>
  /// H? s? t?i tr?ng (?? tính t?i thi?t k?)
        /// </summary>
        public double LoadFactor { get; set; } = 1.0;

    /// <summary>
        /// Clone ??i t??ng
        /// </summary>
     public LoadDefinition Clone()
 {
            return new LoadDefinition
            {
Pattern = Pattern,
       Value = Value,
    Type = Type,
     TargetElement = TargetElement,
       Direction = Direction,
     DistI = DistI,
           DistJ = DistJ,
   IsRelativeDistance = IsRelativeDistance,
      LoadFactor = LoadFactor
     };
  }

     public override string ToString()
   {
            string unit = Type == LoadType.DistributedLine ? "kN/m" :
             Type == LoadType.UniformArea ? "kN/m²" : "kN";
return $"{Pattern}: {Value:0.00} {unit} ({Type}) -> {TargetElement}";
        }
    }

    /// <summary>
    /// Interface chung cho m?i ph?n t? có kh? n?ng truy?n t?i tr?ng sang SAP2000.
    /// Các l?p implement: WallData, BeamData, SlabData, ColumnData...
    /// 
  /// Workflow:
    /// 1. Ph?n t? tính toán t?i tr?ng -> thêm vào Loads
    /// 2. SyncEngine ??c Loads và Mappings
/// 3. SyncEngine phân lu?ng x? lý theo LoadType
    /// </summary>
    public interface ILoadBearing
    {
     /// <summary>
        /// Danh sách các t?i tr?ng ?ã tính toán s?n sàng gán vào SAP
        /// </summary>
        List<LoadDefinition> Loads { get; set; }

        /// <summary>
        /// Danh sách mapping sang ??i t??ng SAP2000 (Frame/Area)
     /// </summary>
        List<MappingRecord> Mappings { get; set; }

      /// <summary>
        /// Tính toán và ?i?n t?i tr?ng vào danh sách Loads.
        /// M?i l?p s? implement logic tính toán riêng.
        /// </summary>
        void CalculateLoads();

        /// <summary>
        /// Xóa t?t c? t?i tr?ng ?ã tính
        /// </summary>
        void ClearLoads();

        /// <summary>
    /// Ki?m tra có t?i tr?ng ?? gán không
 /// </summary>
        bool HasLoads { get; }
    }
}
