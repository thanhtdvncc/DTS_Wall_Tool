using System.Collections.Generic;

namespace DTS_Engine.Core.Data
{
    /// <summary>
    /// Dữ liệu Lanh tô (Lintel) - Dầm ngắn trên cửa - Kế thừa từ ElementData.
    /// </summary>
    public class LintelData : ElementData
    {
        #region Identity Override

        public override ElementType ElementType => ElementType.Lintel;

        #endregion

        #region Lintel-Specific Properties

        /// <summary>
        /// Chiều rộng lanh tô (mm)
        /// </summary>
        public double? Width { get; set; } = null;

        /// <summary>
        /// Chiều cao lanh tô (mm)
        /// </summary>
        public new double? Height { get; set; } = null;

        /// <summary>
        /// Chiều dài lanh tô (mm)
        /// </summary>
        public double? Length { get; set; } = null;

        /// <summary>
        /// Loại lanh tô (VD: "L120x200", "L100x150")
        /// </summary>
        public string LintelType { get; set; } = null;

        /// <summary>
        /// Vật liệu
        /// </summary>
        public string Material { get; set; } = "Concrete";

        /// <summary>
        /// Mác bê tông
        /// </summary>
        public string ConcreteGrade { get; set; } = "C25";

        #endregion

        #region Override Methods

        public override bool HasValidData()
        {
            return (Width.HasValue && Height.HasValue) || !string.IsNullOrEmpty(LintelType);
        }

        public override ElementData Clone()
        {
            var clone = new LintelData
            {
                Width = Width,
                Height = Height,
                Length = Length,
                LintelType = LintelType,
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
            if (Height.HasValue) dict["xHeight"] = Height.Value;
            if (Length.HasValue) dict["xLength"] = Length.Value;
            if (!string.IsNullOrEmpty(LintelType)) dict["xLintelType"] = LintelType;
            if (!string.IsNullOrEmpty(Material)) dict["xMaterial"] = Material;
            // NOTE: xConcreteGrade không còn được ghi - có thể suy ra từ xMaterial

            return dict;
        }

        public override void FromDictionary(Dictionary<string, object> dict)
        {
            ReadBaseProperties(dict);

            if (dict.TryGetValue("xWidth", out var w)) Width = ConvertToDouble(w);
            if (dict.TryGetValue("xHeight", out var h)) Height = ConvertToDouble(h);
            if (dict.TryGetValue("xLength", out var l)) Length = ConvertToDouble(l);
            if (dict.TryGetValue("xLintelType", out var lt)) LintelType = lt?.ToString();
            if (dict.TryGetValue("xMaterial", out var mat)) Material = mat?.ToString();
            // NOTE: xConcreteGrade không còn được đọc - có thể suy ra từ xMaterial
        }

        #endregion

        public void EnsureLintelType()
        {
            if (string.IsNullOrEmpty(LintelType) && Width.HasValue && Height.HasValue)
            {
                LintelType = $"L{(int)Width.Value}x{(int)Height.Value}";
            }
        }

        public override string ToString()
        {
            string sizeStr = LintelType ?? $"{Width ?? 0}x{Height ?? 0}";
            return $"Lintel[{sizeStr}] L={Length ?? 0}mm";
        }
    }
}
