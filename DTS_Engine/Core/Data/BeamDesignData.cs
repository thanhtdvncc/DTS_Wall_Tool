using System;
using System.Collections.Generic;

namespace DTS_Engine.Core.Data
{
    /// <summary>
    /// Chứa dữ liệu kết quả thiết kế từ SAP2000 cho một Dầm.
    /// Lưu trữ cả Raw Area (từ SAP) VÀ Phương án bố trí thép (từ Calculate).
    /// QUAN TRỌNG: Dùng chung 1 class để tránh ghi đè XData.
    /// </summary>
    public class BeamResultData : ElementData
    {
        public BeamResultData()
        {
        }

        // ===== Required ElementData implementations =====
        public override ElementType ElementType => ElementType.Beam;
        public override string XType => "REBAR_DATA";

        public override bool HasValidData()
        {
            return TopArea != null && TopArea.Length == 3;
        }

        public override ElementData Clone()
        {
            var clone = new BeamResultData();
            CopyBaseTo(clone);
            clone.TopArea = (double[])TopArea?.Clone();
            clone.BotArea = (double[])BotArea?.Clone();
            clone.TorsionArea = (double[])TorsionArea?.Clone();
            clone.ShearArea = (double[])ShearArea?.Clone();
            clone.TTArea = (double[])TTArea?.Clone();
            clone.DesignCombo = DesignCombo;
            clone.SectionName = SectionName;
            clone.Width = Width;
            clone.SectionHeight = SectionHeight;
            clone.TorsionFactorUsed = TorsionFactorUsed;
            clone.TopRebarString = (string[])TopRebarString?.Clone();
            clone.BotRebarString = (string[])BotRebarString?.Clone();
            clone.TopAreaProv = (double[])TopAreaProv?.Clone();
            clone.BotAreaProv = (double[])BotAreaProv?.Clone();
            clone.StirrupString = (string[])StirrupString?.Clone();
            clone.WebBarString = (string[])WebBarString?.Clone();
            clone.BeamName = BeamName;
            // SAP Mapping
            clone.SapElementName = SapElementName;
            clone.MappingSource = MappingSource;
            return clone;
        }

        // ===== Raw Data từ SAP (cm2) =====
        // Array[3]: 0=Start, 1=Mid, 2=End
        public double[] TopArea { get; set; } = new double[3];
        public double[] BotArea { get; set; } = new double[3];
        public double[] TorsionArea { get; set; } = new double[3]; // TLArea (Al) - Longitudinal Torsion
        public double[] ShearArea { get; set; } = new double[3]; // VMajor Shear Area (Av/s)
        public double[] TTArea { get; set; } = new double[3]; // Transverse Torsion (At/s) cm2/cm

        public string DesignCombo { get; set; }

        // ===== SAP Mapping (NEW - Origin/Link Integration) =====
        /// <summary>
        /// Tên phần tử SAP đã mapping (VD: "580", "B12")
        /// </summary>
        public string SapElementName { get; set; }

        /// <summary>
        /// Nguồn gốc mapping: "XData" | "Coordinate" | "Manual"
        /// </summary>
        public string MappingSource { get; set; } = "Coordinate";

        // ===== Section Info =====
        public string SectionName { get; set; }
        public double Width { get; set; } // cm
        public double SectionHeight { get; set; } // cm (Depth t3)

        // ===== Cấu hình tính toán =====
        public double TorsionFactorUsed { get; set; } = 0.25;

        // ===== Phương án bố trí thép dọc =====
        public string[] TopRebarString { get; set; } = new string[3];
        public string[] BotRebarString { get; set; } = new string[3];
        public double[] TopAreaProv { get; set; } = new double[3];
        public double[] BotAreaProv { get; set; } = new double[3];

        // ===== Phương án thép đai & thép sườn =====
        public string[] StirrupString { get; set; } = new string[3]; // VD: "d8a150"
        public string[] WebBarString { get; set; } = new string[3];  // VD: "2d12"

        // ===== Beam Name (from Naming command) =====
        public string BeamName { get; set; }

        public override Dictionary<string, object> ToDictionary()
        {
            var dict = new Dictionary<string, object>();
            WriteBaseProperties(dict);
            // Raw Data
            dict["TopArea"] = TopArea;
            dict["BotArea"] = BotArea;
            dict["TorsionArea"] = TorsionArea;
            dict["ShearArea"] = ShearArea;
            dict["TTArea"] = TTArea;
            dict["DesignCombo"] = DesignCombo;
            // Section
            dict["SectionName"] = SectionName;
            dict["Width"] = Width;
            dict["SectionHeight"] = SectionHeight;
            dict["TorsionFactorUsed"] = TorsionFactorUsed;
            // Longitudinal Solution
            dict["TopRebarString"] = TopRebarString;
            dict["BotRebarString"] = BotRebarString;
            dict["TopAreaProv"] = TopAreaProv;
            dict["BotAreaProv"] = BotAreaProv;
            // Stirrup & Web Solution
            dict["StirrupString"] = StirrupString;
            dict["WebBarString"] = WebBarString;
            dict["BeamName"] = BeamName;
            // SAP Mapping
            if (!string.IsNullOrEmpty(SapElementName)) dict["SapElementName"] = SapElementName;
            if (!string.IsNullOrEmpty(MappingSource)) dict["MappingSource"] = MappingSource;
            return dict;
        }

        public override void FromDictionary(Dictionary<string, object> dict)
        {
            ReadBaseProperties(dict);
            // Raw
            if (dict.TryGetValue("TopArea", out var t)) TopArea = ConvertToDoubleArray(t);
            if (dict.TryGetValue("BotArea", out var b)) BotArea = ConvertToDoubleArray(b);
            if (dict.TryGetValue("TorsionArea", out var tor)) TorsionArea = ConvertToDoubleArray(tor);
            if (dict.TryGetValue("ShearArea", out var shear)) ShearArea = ConvertToDoubleArray(shear);
            if (dict.TryGetValue("TTArea", out var tt)) TTArea = ConvertToDoubleArray(tt);
            if (dict.TryGetValue("DesignCombo", out var dc)) DesignCombo = dc?.ToString();
            // Section
            if (dict.TryGetValue("SectionName", out var sn)) SectionName = sn?.ToString();
            if (dict.TryGetValue("Width", out var w)) Width = Convert.ToDouble(w);
            if (dict.TryGetValue("SectionHeight", out var h)) SectionHeight = Convert.ToDouble(h);
            else if (dict.TryGetValue("Height", out var h2)) SectionHeight = Convert.ToDouble(h2); // Backward compat
            if (dict.TryGetValue("TorsionFactorUsed", out var tf)) TorsionFactorUsed = Convert.ToDouble(tf);
            // Longitudinal
            if (dict.TryGetValue("TopRebarString", out var trs)) TopRebarString = ConvertToStringArray(trs);
            if (dict.TryGetValue("BotRebarString", out var brs)) BotRebarString = ConvertToStringArray(brs);
            if (dict.TryGetValue("TopAreaProv", out var tap)) TopAreaProv = ConvertToDoubleArray(tap);
            if (dict.TryGetValue("BotAreaProv", out var bap)) BotAreaProv = ConvertToDoubleArray(bap);
            // Stirrup & Web
            if (dict.TryGetValue("StirrupString", out var ss)) StirrupString = ConvertToStringArray(ss);
            if (dict.TryGetValue("WebBarString", out var ws)) WebBarString = ConvertToStringArray(ws);
            if (dict.TryGetValue("BeamName", out var bn)) BeamName = bn?.ToString();
            // SAP Mapping
            if (dict.TryGetValue("SapElementName", out var sapN)) SapElementName = sapN?.ToString();
            if (dict.TryGetValue("MappingSource", out var mapS)) MappingSource = mapS?.ToString();
        }

        private double[] ConvertToDoubleArray(object obj)
        {
            if (obj is object[] arr)
            {
                double[] res = new double[arr.Length];
                for (int i = 0; i < arr.Length; i++) res[i] = Convert.ToDouble(arr[i]);
                return res;
            }
            if (obj is System.Collections.ArrayList list)
            {
                double[] res = new double[list.Count];
                for (int i = 0; i < list.Count; i++) res[i] = Convert.ToDouble(list[i]);
                return res;
            }
            return new double[3];
        }

        private string[] ConvertToStringArray(object obj)
        {
            if (obj is object[] arr)
            {
                string[] res = new string[arr.Length];
                for (int i = 0; i < arr.Length; i++) res[i] = arr[i]?.ToString();
                return res;
            }
            if (obj is System.Collections.ArrayList list)
            {
                string[] res = new string[list.Count];
                for (int i = 0; i < list.Count; i++) res[i] = list[i]?.ToString();
                return res;
            }
            return new string[3];
        }
    }

    /// <summary>
    /// [DEPRECATED] Dùng BeamResultData thay thế để tránh ghi đè XData.
    /// </summary>
    [Obsolete("Use BeamResultData instead. This class is kept for backward compatibility.")]
    public class BeamRebarSolution : ElementData
    {
        public override ElementType ElementType => ElementType.Beam;
        public override string XType => "REBAR_SOLUTION";

        public override bool HasValidData()
        {
            return TopRebarString != null && TopRebarString.Length == 3;
        }

        public override ElementData Clone()
        {
            var clone = new BeamRebarSolution();
            CopyBaseTo(clone);
            clone.TopRebarString = (string[])TopRebarString?.Clone();
            clone.BotRebarString = (string[])BotRebarString?.Clone();
            clone.TopAreaProv = (double[])TopAreaProv?.Clone();
            clone.BotAreaProv = (double[])BotAreaProv?.Clone();
            return clone;
        }

        // Chuỗi thép hiển thị (VD: "3d20 + 2d25")
        // Array[3]: Start, Mid, End
        public string[] TopRebarString { get; set; } = new string[3];
        public string[] BotRebarString { get; set; } = new string[3];

        // Diện tích thực tế đã bố trí (cm2)
        public double[] TopAreaProv { get; set; } = new double[3];
        public double[] BotAreaProv { get; set; } = new double[3];

        public override Dictionary<string, object> ToDictionary()
        {
            var dict = new Dictionary<string, object>();
            WriteBaseProperties(dict);
            dict["TopRebarString"] = TopRebarString;
            dict["BotRebarString"] = BotRebarString;
            dict["TopAreaProv"] = TopAreaProv;
            dict["BotAreaProv"] = BotAreaProv;
            return dict;
        }

        public override void FromDictionary(Dictionary<string, object> dict)
        {
            ReadBaseProperties(dict);
            if (dict.TryGetValue("TopRebarString", out var tr)) TopRebarString = ConvertToStringArray(tr);
            if (dict.TryGetValue("BotRebarString", out var br)) BotRebarString = ConvertToStringArray(br);
            if (dict.TryGetValue("TopAreaProv", out var ta)) TopAreaProv = ConvertToDoubleArray(ta);
            if (dict.TryGetValue("BotAreaProv", out var ba)) BotAreaProv = ConvertToDoubleArray(ba);
        }

        private string[] ConvertToStringArray(object obj)
        {
            if (obj is object[] arr)
            {
                string[] res = new string[arr.Length];
                for (int i = 0; i < arr.Length; i++) res[i] = arr[i]?.ToString();
                return res;
            }
            if (obj is System.Collections.ArrayList list)
            {
                string[] res = new string[list.Count];
                for (int i = 0; i < list.Count; i++) res[i] = list[i]?.ToString();
                return res;
            }
            return new string[3];
        }
        private double[] ConvertToDoubleArray(object obj)
        {
            if (obj is object[] arr)
            {
                double[] res = new double[arr.Length];
                for (int i = 0; i < arr.Length; i++) res[i] = Convert.ToDouble(arr[i]);
                return res;
            }
            if (obj is System.Collections.ArrayList list)
            {
                double[] res = new double[list.Count];
                for (int i = 0; i < list.Count; i++) res[i] = Convert.ToDouble(list[i]);
                return res;
            }
            return new double[3];
        }
    }

    /// <summary>
    /// Cài đặt tính toán Rebar (Global Settings).
    /// </summary>
    public class RebarSettings
    {
        private static RebarSettings _instance;
        public static RebarSettings Instance => _instance ?? (_instance = new RebarSettings());

        // ===== 1. ZONE RATIOS (Chia vùng chiều dài dầm) =====
        // Dùng để quét Max nội lực trong từng vùng
        // Mặc định: Start 25% - Mid 50% - End 25%
        public double ZoneRatioStart { get; set; } = 0.25;  // 0 → 0.25L
        public double ZoneRatioEnd { get; set; } = 0.25;    // 0.75L → L
        // Mid tự động = 1 - Start - End = 0.5

        // ===== 2. TORSION FACTOR (Phân bổ xoắn vào tiết diện) =====
        // Dùng trong công thức: As = Aflex + Al × Factor
        // Mặc định: 0.25 (chia đều 4 mặt tiết diện)
        public double TorsionFactorTop { get; set; } = 0.25;
        public double TorsionFactorBot { get; set; } = 0.25;
        public double TorsionFactorSide { get; set; } = 0.50; // Phần còn lại cho Web/Sườn

        // Backward compatibility
        [Obsolete("Use TorsionFactorTop/Bot instead")]
        public double TorsionDistributionFactor
        {
            get => TorsionFactorTop;
            set { TorsionFactorTop = value; TorsionFactorBot = value; TorsionFactorSide = 1 - 2 * value; }
        }
        public double TorsionRatioTop { get => TorsionFactorTop; set => TorsionFactorTop = value; }
        public double TorsionRatioBot { get => TorsionFactorBot; set => TorsionFactorBot = value; }
        public double TorsionRatioSide { get => TorsionFactorSide; set => TorsionFactorSide = value; }

        // ===== 3. COVER =====
        public double CoverTop { get; set; } = 35.0; // mm
        public double CoverBot { get; set; } = 35.0; // mm

        // ===== 4. LONGITUDINAL REBAR =====
        public List<int> PreferredDiameters { get; set; } = new List<int> { 16, 18, 20, 22, 25 };
        public double MinSpacing { get; set; } = 30.0; // mm
        public bool MaxBotRebar { get; set; } = true;

        // ===== 5. STIRRUP (Thép đai) =====
        public int StirrupDiameter { get; set; } = 8;  // mm
        public int StirrupLegs { get; set; } = 2;       // Số nhánh
        public List<int> StirrupSpacings { get; set; } = new List<int> { 100, 150, 200, 250 };

        // ===== 6. WEB BARS (Thép sườn/giá) =====
        public int WebBarDiameter { get; set; } = 12;   // mm
        public double WebBarMinHeight { get; set; } = 700; // mm
    }
}
