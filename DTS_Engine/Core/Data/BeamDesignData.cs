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
            clone.BeamType = BeamType;
            clone.BelongToGroup = BelongToGroup;
            return clone;
        }

        // ===== Raw Data từ SAP (cm2) =====
        // Array[3]: 0=Start, 1=Mid, 2=End

        public double[] TopArea { get; set; } = new double[3];
        public double[] BotArea { get; set; } = new double[3];

        /// <summary>
        /// TLArea: Total Longitudinal Rebar Area for Torsion [L2] (cm2)
        /// Tổng diện tích cốt dọc chịu xoắn (Al). Cần phân bổ vào Top/Bot/Side.
        /// </summary>
        public double[] TorsionArea { get; set; } = new double[3];

        /// <summary>
        /// VmajorArea: Transverse Shear Reinforcing per Unit Length [L2/L] (cm2/cm)
        /// Diện tích đai cắt trên 1 đơn vị dài (Av/s).
        /// </summary>
        public double[] ShearArea { get; set; } = new double[3];

        /// <summary>
        /// TTArea: Transverse Torsional Shear Reinforcing per Unit Length [L2/L] (cm2/cm)
        /// Diện tích đai xoắn trên 1 đơn vị dài (At/s).
        /// </summary>
        public double[] TTArea { get; set; } = new double[3];

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

        // ===== GROUPING INFO (Persistent) =====
        /// <summary>
        /// Loại dầm: "Girder" (Chính) hoặc "Beam" (Phụ).
        /// Determines Curtailment Rules.
        /// </summary>
        public string BeamType { get; set; } = "Beam";

        /// <summary>
        /// Support tại đầu I (Start): 1=cột/tường, 0=dầm khác/tự do
        /// Dùng để nhận diện Girder: 2 cột = Girder
        /// </summary>
        public int SupportI { get; set; } = 0;

        /// <summary>
        /// Support tại đầu J (End): 1=cột/tường, 0=dầm khác/tự do
        /// </summary>
        public int SupportJ { get; set; } = 0;

        /// <summary>
        /// Tên trục lưới nằm trên (VD: "A", "1").
        /// Quy tắc Girder: 1 cột + có AxisName = Girder
        /// </summary>
        public string AxisName { get; set; }

        /// <summary>
        /// Tên Group mà dầm này thuộc về (VD: "G2-B1").
        /// Dùng để khôi phục Group khi mở lại bản vẽ.
        /// </summary>
        public string BelongToGroup { get; set; }

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

            // Raw Data - Round to 6 decimal places to reduce XData size
            dict["TopArea"] = RoundArray(TopArea);
            dict["BotArea"] = RoundArray(BotArea);
            dict["TorsionArea"] = RoundArray(TorsionArea);
            dict["ShearArea"] = RoundArray(ShearArea);
            dict["TTArea"] = RoundArray(TTArea);
            dict["DesignCombo"] = DesignCombo;

            // Section (Map to Standard mm keys to avoid duplication)
            dict["xSectionName"] = SectionName;
            // Explicitly round single values too
            dict["xWidth"] = Math.Round(Width * 10.0, 6);          // cm -> mm
            dict["xDepth"] = Math.Round(SectionHeight * 10.0, 6);  // cm -> mm
            dict["TorsionFactorUsed"] = Math.Round(TorsionFactorUsed, 6);

            // Longitudinal Solution
            dict["TopRebarString"] = TopRebarString;
            dict["BotRebarString"] = BotRebarString;
            // REMOVED: TopAreaProv/BotAreaProv - can be calculated from RebarString using ParseRebarArea

            // Stirrup & Web Solution
            dict["StirrupString"] = StirrupString;
            dict["WebBarString"] = WebBarString;
            dict["BeamName"] = BeamName;

            // SAP Mapping
            if (!string.IsNullOrEmpty(SapElementName)) dict["SapElementName"] = SapElementName;
            if (!string.IsNullOrEmpty(MappingSource)) dict["MappingSource"] = MappingSource;

            // Grouping Info
            if (!string.IsNullOrEmpty(BelongToGroup)) dict["BelongToGroup"] = BelongToGroup;
            if (!string.IsNullOrEmpty(BeamType)) dict["xBeamType"] = BeamType; // Map to xBeamType standard

            // Support Info (for Girder detection)
            dict["SupportI"] = SupportI;
            dict["SupportJ"] = SupportJ;
            if (!string.IsNullOrEmpty(AxisName)) dict["xOnAxis"] = AxisName;

            return dict;
        }

        /// <summary>
        /// Round array values to 6 decimal places
        /// </summary>
        private static double[] RoundArray(double[] arr)
        {
            if (arr == null) return null;
            var result = new double[arr.Length];
            for (int i = 0; i < arr.Length; i++)
            {
                result[i] = Math.Round(arr[i], 6);
            }
            return result;
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

            // Section - Prioritize Standard xKeys (mm)
            if (dict.TryGetValue("xSectionName", out var xsn)) SectionName = xsn?.ToString();
            else if (dict.TryGetValue("SectionName", out var sn)) SectionName = sn?.ToString();

            if (dict.TryGetValue("xWidth", out var xw)) Width = Convert.ToDouble(xw) / 10.0;
            else if (dict.TryGetValue("Width", out var w)) Width = Convert.ToDouble(w);

            if (dict.TryGetValue("xDepth", out var xd)) SectionHeight = Convert.ToDouble(xd) / 10.0;
            else if (dict.TryGetValue("SectionHeight", out var sh)) SectionHeight = Convert.ToDouble(sh);
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

            // Mapping
            if (dict.TryGetValue("SapElementName", out var sapN)) SapElementName = sapN?.ToString();
            if (dict.TryGetValue("MappingSource", out var mapS)) MappingSource = mapS?.ToString();

            // Grouping (Standard xBeamType)
            if (dict.TryGetValue("xBeamType", out var xbt)) BeamType = xbt?.ToString();
            else if (dict.TryGetValue("BeamType", out var bt)) BeamType = bt?.ToString();

            if (dict.TryGetValue("BelongToGroup", out var btg)) BelongToGroup = btg?.ToString();

            // Support
            if (dict.TryGetValue("SupportI", out var si)) SupportI = Convert.ToInt32(si);
            else if (dict.TryGetValue("xSupport_I", out var xsi)) SupportI = Convert.ToInt32(xsi);

            if (dict.TryGetValue("SupportJ", out var sj)) SupportJ = Convert.ToInt32(sj);
            else if (dict.TryGetValue("xSupport_J", out var xsj)) SupportJ = Convert.ToInt32(xsj);

            if (dict.TryGetValue("xOnAxis", out var xoa)) AxisName = xoa?.ToString();
            else if (dict.TryGetValue("OnAxis", out var oa)) AxisName = oa?.ToString();
        }

        private double[] ConvertToDoubleArray(object obj)
        {
            var result = new double[3];
            if (obj == null) return result;

            if (obj is double[] arr)
            {
                for (int i = 0; i < 3 && i < arr.Length; i++) result[i] = arr[i];
                return result;
            }

            // Robust IEnumerable handling (ArrayList, JArray, List<object>...)
            if (obj is System.Collections.IEnumerable enumerable && !(obj is string))
            {
                int i = 0;
                foreach (var item in enumerable)
                {
                    if (i >= 3) break;
                    if (item != null)
                    {
                        try { result[i] = Convert.ToDouble(item); } catch { }
                    }
                    i++;
                }
            }
            return result;
        }

        private string[] ConvertToStringArray(object obj)
        {
            var result = new string[3];
            if (obj == null) return result;

            if (obj is string[] arr)
            {
                for (int i = 0; i < 3 && i < arr.Length; i++) result[i] = arr[i];
                return result;
            }

            // Robust IEnumerable handling (ArrayList, JArray, List<object>...)
            if (obj is System.Collections.IEnumerable enumerable && !(obj is string))
            {
                int i = 0;
                foreach (var item in enumerable)
                {
                    if (i >= 3) break;
                    if (item != null) result[i] = item.ToString();
                    i++;
                }
            }
            return result;
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
            // REMOVED: TopAreaProv/BotAreaProv - calculated from RebarString when needed
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
    /// [DEPRECATED] Cài đặt tính toán Rebar (Global Settings).
    /// Sử dụng DtsSettings.Beam thay thế cho tất cả rebar calculation settings.
    /// </summary>
    [Obsolete("Use DtsSettings.Beam instead. This class is kept for backward compatibility only.")]
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
        public double CoverSide { get; set; } = 25.0; // mm (2 bên hông)

        // ===== 4. LONGITUDINAL REBAR =====
        public List<int> PreferredDiameters { get; set; } = new List<int> { 16, 18, 20, 22, 25 };
        public double MinSpacing { get; set; } = 30.0; // mm
        public bool MaxBotRebar { get; set; } = true;

        // ===== 5. STIRRUP (Thép đai) =====
        public List<int> StirrupDiameters { get; set; } = new List<int> { 8, 10 }; // Danh sách đường kính đai
        public int StirrupDiameter { get; set; } = 8;  // mm (Backward compat)
        public int StirrupLegs { get; set; } = 2;       // Số nhánh mặc định
        public List<int> StirrupSpacings { get; set; } = new List<int> { 100, 150, 200, 250 };

        /// <summary>
        /// Cho phép bố trí nhánh lẻ (3, 5...) cho thép đai.
        /// </summary>
        public bool AllowOddLegs { get; set; } = false;

        /// <summary>
        /// Tự động tính số nhánh theo bề rộng dầm.
        /// Nếu false, dùng StirrupLegs cố định.
        /// </summary>
        public bool AutoLegsFromWidth { get; set; } = true;

        /// <summary>
        /// Chuỗi quy tắc auto legs: "250-2 400-3 600-4 800-5"
        /// Nghĩa: b<=250→2 nhánh, b<=400→3 nhánh...
        /// </summary>
        public string AutoLegsRules { get; set; } = "250-2 400-3 600-4 800-5";

        // ===== 6. WEB BARS (Thép sườn/giá) =====
        public List<int> WebBarDiameters { get; set; } = new List<int> { 12, 14 }; // Danh sách đường kính sườn
        public int WebBarDiameter { get; set; } = 12;   // mm (Backward compat)
        public double WebBarMinHeight { get; set; } = 700; // mm

        // ===== 7. BEAM NAMING (Đặt tên dầm) =====
        public string BeamPrefix { get; set; } = "B";
        public string GirderPrefix { get; set; } = "G";
        public string BeamSuffix { get; set; } = "";
        public bool GroupByAxis { get; set; } = true;              // Nhóm theo trục
        public bool MergeSameSection { get; set; } = true;         // Gộp dầm cùng section & rebar
        public bool AutoRenameOnSectionChange { get; set; } = false; // Tự động rename khi section đổi

        /// <summary>
        /// Góc bắt đầu quét dầm: 0=Top-Left, 1=Top-Right, 2=Bot-Left, 3=Bot-Right
        /// </summary>
        public int SortCorner { get; set; } = 0;

        /// <summary>
        /// Hướng ưu tiên quét: 0=Horizontal (quét X trước), 1=Vertical (quét Y trước)
        /// </summary>
        public int SortDirection { get; set; } = 0;
    }
}
