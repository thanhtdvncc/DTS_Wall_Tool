using System.Collections.Generic;

namespace DTS_Wall_Tool.Core.Data
{
    /// <summary>
    /// Dữ liệu Cột - Kế thừa từ ElementData. 
    /// </summary>
    public class ColumnData : ElementData
    {
        #region Identity Override

        public override ElementType ElementType => ElementType.Column;

        #endregion

        #region Column-Specific Properties

        /// <summary>
        /// Chiều rộng cột theo phương X (mm)
        /// </summary>
        public double? Width { get; set; } = null;

        /// <summary>
        /// Chiều sâu cột theo phương Y (mm)
        /// </summary>
        public double? Depth { get; set; } = null;

        /// <summary>
        /// Tên tiết diện (VD: "C400x600", "C300x300")
        /// </summary>
        public string SectionName { get; set; } = null;

        /// <summary>
        /// Alias cho SectionName để tương thích với LabelUtils
        /// </summary>
        public string ColumnType
        {
            get => SectionName;
            set => SectionName = value;
        }

        /// <summary>
        /// Loại tiết diện (Rectangular, Circular, L-Shape...)
        /// </summary>
        public string SectionType { get; set; } = "Rectangular";

        /// <summary>
        /// Đường kính (cho cột tròn) (mm)
        /// </summary>
        public double? Diameter { get; set; } = null;

        /// <summary>
        /// Vật liệu (Concrete, Steel...)
        /// </summary>
        public string Material { get; set; } = "Concrete";

        /// <summary>
        /// Mác bê tông (VD: "C30", "C40")
        /// </summary>
        public string ConcreteGrade { get; set; } = "C30";

        #endregion

        #region Override Methods

        public override bool HasValidData()
        {
            return (Width.HasValue && Depth.HasValue) ||
                   Diameter.HasValue ||
                   !string.IsNullOrEmpty(SectionName);
        }

        public override ElementData Clone()
        {
            var clone = new ColumnData
            {
                Width = Width,
                Depth = Depth,
                SectionName = SectionName,
                SectionType = SectionType,
                Diameter = Diameter,
                Material = Material,
                ConcreteGrade = ConcreteGrade
            };

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
            if (!string.IsNullOrEmpty(SectionType)) dict["xSectionType"] = SectionType;
            if (Diameter.HasValue) dict["xDiameter"] = Diameter.Value;
            if (!string.IsNullOrEmpty(Material)) dict["xMaterial"] = Material;
            if (!string.IsNullOrEmpty(ConcreteGrade)) dict["xConcreteGrade"] = ConcreteGrade;

            return dict;
        }

        public override void FromDictionary(Dictionary<string, object> dict)
        {
            ReadBaseProperties(dict);

            if (dict.TryGetValue("xWidth", out var w)) Width = ConvertToDouble(w);
            if (dict.TryGetValue("xDepth", out var d)) Depth = ConvertToDouble(d);
            if (dict.TryGetValue("xSectionName", out var sn)) SectionName = sn?.ToString();
            if (dict.TryGetValue("xSectionType", out var st)) SectionType = st?.ToString();
            if (dict.TryGetValue("xDiameter", out var dia)) Diameter = ConvertToDouble(dia);
            if (dict.TryGetValue("xMaterial", out var mat)) Material = mat?.ToString();
            if (dict.TryGetValue("xConcreteGrade", out var cg)) ConcreteGrade = cg?.ToString();
        }

        #endregion

        #region Column-Specific Methods

        /// <summary>
        /// Tự động tạo SectionName từ Width và Depth
        /// </summary>
        public void EnsureSectionName()
        {
            if (string.IsNullOrEmpty(SectionName))
            {
                if (Width.HasValue && Depth.HasValue)
                {
                    SectionName = $"C{(int)Width.Value}x{(int)Depth.Value}";
                }
                else if (Diameter.HasValue)
                {
                    SectionName = $"D{(int)Diameter.Value}";
                }
            }
        }

        /// <summary>
        /// Tính diện tích tiết diện (mm2)
        /// </summary>
        public double GetArea()
        {
            if (SectionType == "Circular" && Diameter.HasValue)
            {
                return System.Math.PI * Diameter.Value * Diameter.Value / 4.0;
            }
            else if (Width.HasValue && Depth.HasValue)
            {
                return Width.Value * Depth.Value;
            }
            return 0;
        }

        public override string ToString()
        {
            string sizeStr = SectionName ?? $"{Width ?? 0}x{Depth ?? 0}";
            string linkStatus = IsLinked ? "Linked" : "Unlinked";
            return $"Column[{sizeStr}] {Material}, {linkStatus}";
        }

        #endregion
    }
}