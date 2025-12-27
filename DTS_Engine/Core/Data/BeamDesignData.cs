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
            clone.TopRS = (string[])TopRS?.Clone();
            clone.BotRS = (string[])BotRS?.Clone();
            clone.TopAreaProv = (double[])TopAreaProv?.Clone();
            clone.BotAreaProv = (double[])BotAreaProv?.Clone();
            clone.StirrupAreaProv = (double[])StirrupAreaProv?.Clone();
            clone.StirRS = (string[])StirRS?.Clone();
            clone.WebRS = (string[])WebRS?.Clone();
            clone.BeamName = BeamName;
            // SAP Mapping
            clone.SapElementName = SapElementName;
            // NOTE: MappingSource đã deprecated - không clone
            clone.BeamType = BeamType;
            clone.BelongToGroup = BelongToGroup;

            // Metadata
            clone.TopMoment = (double[])TopMoment?.Clone();
            clone.BotMoment = (double[])BotMoment?.Clone();
            clone.ShearForce = (double[])ShearForce?.Clone();
            clone.TorsionMoment = (double[])TorsionMoment?.Clone();
            clone.TopCombo = (string[])TopCombo?.Clone();
            clone.BotCombo = (string[])BotCombo?.Clone();
            clone.ShearCombo = (string[])ShearCombo?.Clone();
            clone.TorsionCombo = (string[])TorsionCombo?.Clone();
            clone.SapElementNos = (string[])SapElementNos?.Clone();
            clone.LocationMm = (double[])LocationMm?.Clone();

            clone.TopSapNo = (string[])TopSapNo?.Clone();
            clone.TopLocMm = (double[])TopLocMm?.Clone();
            clone.BotSapNo = (string[])BotSapNo?.Clone();
            clone.BotLocMm = (double[])BotLocMm?.Clone();
            clone.ShearSapNo = (string[])ShearSapNo?.Clone();
            clone.ShearLocMm = (double[])ShearLocMm?.Clone();
            clone.TorsionSapNo = (string[])TorsionSapNo?.Clone();
            clone.TorsionLocMm = (double[])TorsionLocMm?.Clone();

            clone.ConcreteGrade = ConcreteGrade;
            clone.SteelGrade = SteelGrade;

            clone.SectionLabel = SectionLabel;
            clone.SectionLabelLocked = SectionLabelLocked;

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

        // ===== Metadata cho thuyết minh (3 vị trí) =====
        public double[] TopMoment { get; set; } = new double[3];
        public double[] BotMoment { get; set; } = new double[3];
        public double[] ShearForce { get; set; } = new double[3];
        public double[] TorsionMoment { get; set; } = new double[3];

        public string[] TopCombo { get; set; } = new string[3];
        public string[] BotCombo { get; set; } = new string[3];
        public string[] ShearCombo { get; set; } = new string[3];
        public string[] TorsionCombo { get; set; } = new string[3];

        public string[] SapElementNos { get; set; } = new string[3]; // Legacy / Ref
        public double[] LocationMm { get; set; } = new double[3];   // Legacy / Ref

        public string[] TopSapNo { get; set; } = new string[3];
        public double[] TopLocMm { get; set; } = new double[3];

        public string[] BotSapNo { get; set; } = new string[3];
        public double[] BotLocMm { get; set; } = new double[3];

        public string[] ShearSapNo { get; set; } = new string[3];
        public double[] ShearLocMm { get; set; } = new double[3];

        public string[] TorsionSapNo { get; set; } = new string[3];
        public double[] TorsionLocMm { get; set; } = new double[3];

        public string ConcreteGrade { get; set; } = "";
        public string SteelGrade { get; set; } = "";

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
        public string BeamType { get; set; } = "";

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
        public string[] TopRS { get; set; } = new string[3];
        public string[] BotRS { get; set; } = new string[3];
        public double[] TopAreaProv { get; set; } = new double[3];
        public double[] BotAreaProv { get; set; } = new double[3];
        public double[] StirrupAreaProv { get; set; } = new double[3];

        // ===== Phương án thép đai & thép sườn =====
        public string[] StirRS { get; set; } = new string[3]; // VD: "d8a150"
        public string[] WebRS { get; set; } = new string[3];  // VD: "2d12"

        // ===== Beam Name (from Naming command) =====
        public string BeamName { get; set; }

        public string SectionLabel { get; set; }
        public bool SectionLabelLocked { get; set; }

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

            // Metadata for report
            dict["TopM"] = RoundArray(TopMoment);
            dict["BotM"] = RoundArray(BotMoment);
            dict["ShearV"] = RoundArray(ShearForce);
            dict["TorM"] = RoundArray(TorsionMoment);
            dict["TopC"] = TopCombo;
            dict["BotC"] = BotCombo;
            dict["ShearC"] = ShearCombo;
            dict["TorC"] = TorsionCombo;

            // Section - ISO/IEC 25010: Single Source of Truth (xKeys only)
            dict["xSectionName"] = SectionName;
            dict["xWidth"] = Math.Round(Width * 10.0, 2);          // cm -> mm
            dict["xDepth"] = Math.Round(SectionHeight * 10.0, 2);  // cm -> mm

            if (!string.IsNullOrEmpty(SectionLabel)) dict["xSectionLabel"] = SectionLabel;
            if (SectionLabelLocked) dict["xSectionLabelLocked"] = "1";

            // V6.0: RS properties không còn ghi vào XData
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

            // Report Strings
            dict["TopRS"] = TopRS;
            dict["BotRS"] = BotRS;
            dict["StirRS"] = StirRS;
            dict["WebRS"] = WebRS;

            // Provided Areas
            dict["TopP"] = RoundArray(TopAreaProv);
            dict["BotP"] = RoundArray(BotAreaProv);
            dict["StirP"] = RoundArray(StirrupAreaProv);

            dict["xConcreteGrade"] = ConcreteGrade;
            dict["xSteelGrade"] = SteelGrade;

            dict["xSapNos"] = SapElementNos;
            dict["xLocMm"] = RoundArray(LocationMm);

            dict["xTopSap"] = TopSapNo;
            dict["xTopLoc"] = RoundArray(TopLocMm);
            dict["xBotSap"] = BotSapNo;
            dict["xBotLoc"] = RoundArray(BotLocMm);
            dict["xShearSap"] = ShearSapNo;
            dict["xShearLoc"] = RoundArray(ShearLocMm);
            dict["xTorSap"] = TorsionSapNo;
            dict["xTorLoc"] = RoundArray(TorsionLocMm);

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

            if (dict.TryGetValue("TopM", out var tm)) TopMoment = ConvertToDoubleArray(tm);
            if (dict.TryGetValue("BotM", out var bm)) BotMoment = ConvertToDoubleArray(bm);
            if (dict.TryGetValue("ShearV", out var sv)) ShearForce = ConvertToDoubleArray(sv);
            if (dict.TryGetValue("TorM", out var trm)) TorsionMoment = ConvertToDoubleArray(trm);
            if (dict.TryGetValue("TopC", out var tc)) TopCombo = ConvertToStringArray(tc);
            if (dict.TryGetValue("BotC", out var bc)) BotCombo = ConvertToStringArray(bc);
            if (dict.TryGetValue("ShearC", out var sc)) ShearCombo = ConvertToStringArray(sc);
            if (dict.TryGetValue("TorC", out var trc)) TorsionCombo = ConvertToStringArray(trc);

            // Report Strings
            if (dict.TryGetValue("TopRS", out var trs)) TopRS = ConvertToStringArray(trs);
            if (dict.TryGetValue("BotRS", out var brs)) BotRS = ConvertToStringArray(brs);
            if (dict.TryGetValue("StirRS", out var srs)) StirRS = ConvertToStringArray(srs);
            if (dict.TryGetValue("WebRS", out var wrs)) WebRS = ConvertToStringArray(wrs);

            // Provided Areas
            if (dict.TryGetValue("TopP", out var tp)) TopAreaProv = ConvertToDoubleArray(tp);
            if (dict.TryGetValue("BotP", out var bp)) BotAreaProv = ConvertToDoubleArray(bp);
            if (dict.TryGetValue("StirP", out var stp)) StirrupAreaProv = ConvertToDoubleArray(stp);

            // Section - ISO/IEC 25010: Single Source of Truth (xKeys only, mm)
            if (dict.TryGetValue("xSectionName", out var xsn)) SectionName = xsn?.ToString();
            if (dict.TryGetValue("xWidth", out var xw)) Width = Convert.ToDouble(xw) / 10.0;  // mm → cm
            if (dict.TryGetValue("xDepth", out var xd)) SectionHeight = Convert.ToDouble(xd) / 10.0;  // mm → cm

            if (dict.TryGetValue("xSectionLabel", out var sl)) SectionLabel = sl?.ToString();
            if (dict.TryGetValue("xSectionLabelLocked", out var sll)) SectionLabelLocked = sll?.ToString() == "1";

            // V6.0: RS properties không còn đọc từ XData
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

            if (dict.TryGetValue("xConcreteGrade", out var cg)) ConcreteGrade = cg?.ToString() ?? "B25";
            if (dict.TryGetValue("xSteelGrade", out var sg)) SteelGrade = sg?.ToString() ?? "CB400";

            if (dict.TryGetValue("xSapNos", out var xsnos)) SapElementNos = ConvertToStringArray(xsnos);
            if (dict.TryGetValue("xLocMm", out var xloc)) LocationMm = ConvertToDoubleArray(xloc);

            if (dict.TryGetValue("xTopSap", out var xts)) TopSapNo = ConvertToStringArray(xts);
            if (dict.TryGetValue("xTopLoc", out var xtl)) TopLocMm = ConvertToDoubleArray(xtl);
            if (dict.TryGetValue("xBotSap", out var xbs)) BotSapNo = ConvertToStringArray(xbs);
            if (dict.TryGetValue("xBotLoc", out var xbl)) BotLocMm = ConvertToDoubleArray(xbl);
            if (dict.TryGetValue("xShearSap", out var xss)) ShearSapNo = ConvertToStringArray(xss);
            if (dict.TryGetValue("xShearLoc", out var xsl)) ShearLocMm = ConvertToDoubleArray(xsl);
            if (dict.TryGetValue("xTorSap", out var xtors)) TorsionSapNo = ConvertToStringArray(xtors);
            if (dict.TryGetValue("xTorLoc", out var xtorl)) TorsionLocMm = ConvertToDoubleArray(xtorl);
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
