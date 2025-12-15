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
            // Default xType
        }

        public override string xType => "REBAR_DATA"; // Thống nhất 1 type

        // ===== Raw Data từ SAP (cm2) =====
        // Array[3]: 0=Start, 1=Mid, 2=End
        public double[] TopArea { get; set; } = new double[3];
        public double[] BotArea { get; set; } = new double[3];
        public double[] TorsionArea { get; set; } = new double[3];
        public double[] ShearArea { get; set; } = new double[3]; // VMajor Shear Area

        public string DesignCombo { get; set; }
        
        // ===== Section Info =====
        public string SectionName { get; set; }
        public double Width { get; set; } // cm
        public double Height { get; set; } // cm (Depth t3)

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
            var dict = base.ToDictionary();
            // Raw Data
            dict["TopArea"] = TopArea;
            dict["BotArea"] = BotArea;
            dict["TorsionArea"] = TorsionArea;
            dict["ShearArea"] = ShearArea;
            dict["DesignCombo"] = DesignCombo;
            // Section
            dict["SectionName"] = SectionName;
            dict["Width"] = Width;
            dict["Height"] = Height;
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
            return dict;
        }

        public override void FromDictionary(Dictionary<string, object> dict)
        {
            base.FromDictionary(dict);
            // Raw
            if (dict.TryGetValue("TopArea", out var t)) TopArea = ConvertToDoubleArray(t);
            if (dict.TryGetValue("BotArea", out var b)) BotArea = ConvertToDoubleArray(b);
            if (dict.TryGetValue("TorsionArea", out var tor)) TorsionArea = ConvertToDoubleArray(tor);
            if (dict.TryGetValue("ShearArea", out var shear)) ShearArea = ConvertToDoubleArray(shear);
            if (dict.TryGetValue("DesignCombo", out var dc)) DesignCombo = dc?.ToString();
            // Section
            if (dict.TryGetValue("SectionName", out var sn)) SectionName = sn?.ToString();
            if (dict.TryGetValue("Width", out var w)) Width = Convert.ToDouble(w);
            if (dict.TryGetValue("Height", out var h)) Height = Convert.ToDouble(h);
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
        }

        private double[] ConvertToDoubleArray(object obj)
        {
            if (obj is object[] arr)
            {
                double[] res = new double[arr.Length];
                for(int i=0; i<arr.Length; i++) res[i] = Convert.ToDouble(arr[i]);
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
                for(int i=0; i<arr.Length; i++) res[i] = arr[i]?.ToString();
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
        public override string xType => "REBAR_SOLUTION";

        // Chuỗi thép hiển thị (VD: "3d20 + 2d25")
        // Array[3]: Start, Mid, End
        public string[] TopRebarString { get; set; } = new string[3];
        public string[] BotRebarString { get; set; } = new string[3];

        // Diện tích thực tế đã bố trí (cm2)
        public double[] TopAreaProv { get; set; } = new double[3];
        public double[] BotAreaProv { get; set; } = new double[3];

        public override Dictionary<string, object> ToDictionary()
        {
            var dict = base.ToDictionary();
            dict["TopRebarString"] = TopRebarString;
            dict["BotRebarString"] = BotRebarString;
            dict["TopAreaProv"] = TopAreaProv;
            dict["BotAreaProv"] = BotAreaProv;
            return dict;
        }

        public override void FromDictionary(Dictionary<string, object> dict)
        {
            base.FromDictionary(dict);
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
                for(int i=0; i<arr.Length; i++) res[i] = arr[i]?.ToString();
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
                for(int i=0; i<arr.Length; i++) res[i] = Convert.ToDouble(arr[i]);
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

        // --- Torsion Distribution (3-part ratio) ---
        // Mặc định: 0.25 (Top) + 0.5 (Side/Web) + 0.25 (Bot) = 1.0
        public double TorsionRatioTop { get; set; } = 0.25;
        public double TorsionRatioSide { get; set; } = 0.50;
        public double TorsionRatioBot { get; set; } = 0.25;

        // Backward compatibility (để code cũ không bị lỗi)
        [Obsolete("Use TorsionRatioTop/Bot instead")]
        public double TorsionDistributionFactor 
        { 
            get => TorsionRatioTop; 
            set { TorsionRatioTop = value; TorsionRatioBot = value; TorsionRatioSide = 1 - 2 * value; }
        }

        // --- Cover ---
        public double CoverTop { get; set; } = 35.0; // mm
        public double CoverBot { get; set; } = 35.0; // mm

        // --- Longitudinal Rebar ---
        public List<int> PreferredDiameters { get; set; } = new List<int> { 16, 18, 20, 22, 25 };
        public double MinSpacing { get; set; } = 30.0; // mm
        public bool MaxBotRebar { get; set; } = true;

        // --- Stirrup (Thép đai) ---
        public int StirrupDiameter { get; set; } = 8;  // mm
        public int StirrupLegs { get; set; } = 2;       // Số nhánh
        public List<int> StirrupSpacings { get; set; } = new List<int> { 100, 150, 200, 250 };

        // --- Web Bars (Thép sườn/giá) ---
        public int WebBarDiameter { get; set; } = 12;   // mm
        public double WebBarMinHeight { get; set; } = 700; // mm
    }
}
