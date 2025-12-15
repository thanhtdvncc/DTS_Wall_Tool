using System;
using System.Collections.Generic;

namespace DTS_Engine.Core.Data
{
    /// <summary>
    /// Chứa dữ liệu kết quả thiết kế từ SAP2000 cho một Dầm.
    /// Lưu trữ diện tích thép yêu cầu (Required Area) tại các vị trí quan trọng.
    /// </summary>
    public class BeamResultData : ElementData
    {
        public BeamResultData()
        {
            // Default xType
        }

        public override string xType => "REBAR_RESULT";

        // --- Required Area from SAP (cm2) ---
        // Array[3]: 0=Start, 1=Mid, 2=End

        public double[] TopArea { get; set; } = new double[3];
        public double[] BotArea { get; set; } = new double[3];
        
        // Torsion Longitudinal Area (cm2) - Total for the section
        public double[] TorsionArea { get; set; } = new double[3];

        public string DesignCombo { get; set; }
        
        // Section Info (Snapshot at time of result fetch)
        public string SectionName { get; set; }
        public double Width { get; set; } // cm
        public double Height { get; set; } // cm // Depth (t3)

        // Cấu hình tính toán đã áp dụng (để trace lại)
        public double TorsionFactorUsed { get; set; } = 0.5; // Default 0.5

        public override Dictionary<string, object> ToDictionary()
        {
            var dict = base.ToDictionary();
            dict["TopArea"] = TopArea;
            dict["BotArea"] = BotArea;
            dict["TorsionArea"] = TorsionArea;
            dict["DesignCombo"] = DesignCombo;
            dict["SectionName"] = SectionName;
            dict["Width"] = Width;
            dict["Height"] = Height;
            dict["TorsionFactorUsed"] = TorsionFactorUsed;
            return dict;
        }

        public override void FromDictionary(Dictionary<string, object> dict)
        {
            base.FromDictionary(dict);
            if (dict.TryGetValue("TopArea", out var t)) TopArea = ConvertToDoubleArray(t);
            if (dict.TryGetValue("BotArea", out var b)) BotArea = ConvertToDoubleArray(b);
            if (dict.TryGetValue("TorsionArea", out var tor)) TorsionArea = ConvertToDoubleArray(tor);
            if (dict.TryGetValue("DesignCombo", out var dc)) DesignCombo = dc?.ToString();
            
            if (dict.TryGetValue("SectionName", out var sn)) SectionName = sn?.ToString();
            if (dict.TryGetValue("Width", out var w)) Width = Convert.ToDouble(w);
            if (dict.TryGetValue("Height", out var h)) Height = Convert.ToDouble(h);
            
            if (dict.TryGetValue("TorsionFactorUsed", out var tf)) TorsionFactorUsed = Convert.ToDouble(tf);
        }

        private double[] ConvertToDoubleArray(object obj)
        {
            if (obj is object[] arr)
            {
                double[] res = new double[arr.Length];
                for(int i=0; i<arr.Length; i++) res[i] = Convert.ToDouble(arr[i]);
                return res;
            }
            // Handle array serialized as ArrayList or similar if needed
            // For now assume standard object[] from serializer
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
    /// Lưu trữ giải pháp bố trí thép (User chọn hoặc Auto tính).
    /// </summary>
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

        // --- Torsion ---
        // 0.25 (chia 4 mặt) hoặc 0.5 (dồn top/bot)
        public double TorsionDistributionFactor { get; set; } = 0.25; 

        // --- Cover ---
        public double CoverTop { get; set; } = 35.0; // mm
        public double CoverBot { get; set; } = 35.0; // mm

        // --- Materials ---
        public List<int> PreferredDiameters { get; set; } = new List<int> { 16, 18, 20, 22, 25 };
        
        // --- Strategy ---
        public bool MaxBotRebar { get; set; } = true; // True = Option 1 (Max All), False = Option 2

        public double MinSpacing { get; set; } = 30.0; // mm
    }
}
