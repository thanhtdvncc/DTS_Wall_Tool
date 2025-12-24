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
            // NOTE: MappingSource đã deprecated - không clone
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

        // ===== SAP Mapping =====
        /// <summary>
        /// Tên phần tử SAP đã mapping (VD: "580", "B12")
        /// Lưu vào xSapFrameName trong XData
        /// </summary>
        public string SapElementName { get; set; }

        // NOTE: MappingSource đã xóa - không còn lưu vào XData

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
            // NOTE: DesignCombo và TorsionFactorUsed không còn được ghi vào XData

            // Section - ISO/IEC 25010: Single Source of Truth (xKeys only)
            dict["xSectionName"] = SectionName;
            dict["xWidth"] = Math.Round(Width * 10.0, 2);          // cm -> mm
            dict["xDepth"] = Math.Round(SectionHeight * 10.0, 2);  // cm -> mm

            // V6.0: TopRebarString/BotRebarString/StirrupString/WebBarString không còn ghi vào XData
            // Dùng OptUser là Single Source of Truth
            dict["BeamName"] = BeamName;

            // SAP Mapping - Single Source: xSapFrameName (không ghi SapElementName riêng)
            if (!string.IsNullOrEmpty(SapElementName)) dict["xSapFrameName"] = SapElementName;
            // NOTE: MappingSource đã loại bỏ

            // NOTE: BelongToGroup đã xóa - dùng GroupIdentity
            // NOTE: xBeamType đã xóa - dùng xBeamType từ BeamData

            // Support Info - Single Source: xSupport_I, xSupport_J, xOnAxis
            dict["xSupport_I"] = SupportI;
            dict["xSupport_J"] = SupportJ;
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

            // Raw Area Data (không có fallback - nguồn duy nhất)
            if (dict.TryGetValue("TopArea", out var t)) TopArea = ConvertToDoubleArray(t);
            if (dict.TryGetValue("BotArea", out var b)) BotArea = ConvertToDoubleArray(b);
            if (dict.TryGetValue("TorsionArea", out var tor)) TorsionArea = ConvertToDoubleArray(tor);
            if (dict.TryGetValue("ShearArea", out var shear)) ShearArea = ConvertToDoubleArray(shear);
            if (dict.TryGetValue("TTArea", out var tt)) TTArea = ConvertToDoubleArray(tt);
            // NOTE: DesignCombo và TorsionFactorUsed không còn được đọc từ XData

            // Section - ISO/IEC 25010: Single Source of Truth (xKeys only, mm)
            if (dict.TryGetValue("xSectionName", out var xsn)) SectionName = xsn?.ToString();
            if (dict.TryGetValue("xWidth", out var xw)) Width = Convert.ToDouble(xw) / 10.0;  // mm → cm
            if (dict.TryGetValue("xDepth", out var xd)) SectionHeight = Convert.ToDouble(xd) / 10.0;  // mm → cm

            // V6.0: TopRebarString/BotRebarString/StirrupString/WebBarString không còn đọc từ XData
            // Dùng OptUser là Single Source of Truth
            if (dict.TryGetValue("BeamName", out var bn)) BeamName = bn?.ToString();

            // SAP Mapping - Single Source: xSapFrameName
            if (dict.TryGetValue("xSapFrameName", out var xsfn)) SapElementName = xsfn?.ToString();
            // NOTE: MappingSource không cần đọc - đã loại bỏ

            // Grouping - Single Source: xBeamType
            if (dict.TryGetValue("xBeamType", out var xbt)) BeamType = xbt?.ToString();
            if (dict.TryGetValue("BelongToGroup", out var btg)) BelongToGroup = btg?.ToString();

            // Support - Single Source: xSupport_I, xSupport_J, xOnAxis
            if (dict.TryGetValue("xSupport_I", out var xsi)) SupportI = Convert.ToInt32(xsi);
            if (dict.TryGetValue("xSupport_J", out var xsj)) SupportJ = Convert.ToInt32(xsj);
            if (dict.TryGetValue("xOnAxis", out var xoa)) AxisName = xoa?.ToString();
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

    // NOTE: BeamRebarSolution class đã xóa - thay bằng BeamResultData
    // NOTE: RebarSettings class đã xóa - thay bằng DtsSettings.Beam
}
