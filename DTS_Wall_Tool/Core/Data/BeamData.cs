using System.Collections.Generic;

namespace DTS_Wall_Tool.Core.Data
{
    /// <summary>
    /// Dữ liệu Dầm - Kế thừa từ ElementData.
    /// </summary>
    public class BeamData : ElementData
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
                BeamType = BeamType
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
            if (Length.HasValue) dict["xLength"] = Length.Value;
            if (!string.IsNullOrEmpty(Material)) dict["xMaterial"] = Material;
            if (!string.IsNullOrEmpty(ConcreteGrade)) dict["xConcreteGrade"] = ConcreteGrade;
            if (!string.IsNullOrEmpty(BeamType)) dict["xBeamType"] = BeamType;

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

        public override string ToString()
        {
            string sizeStr = SectionName ?? $"{Width ?? 0}x{Depth ?? 0}";
            string linkStatus = IsLinked ? "Linked" : "Unlinked";
            return $"Beam[{sizeStr}] {BeamType}, {linkStatus}";
        }

        #endregion
    }
}