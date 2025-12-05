using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Engine.Core.Data
{
    /// <summary>
    /// D? li?u thô c?a m?t t?i tr?ng t? SAP2000.
    /// H? tr? t?t c? các lo?i t?i: Frame, Area, Point.
    /// </summary>
    public class RawSapLoad
    {
        /// <summary>Tên ph?n t? trong SAP2000</summary>
        public string ElementName { get; set; }

        /// <summary>Load Pattern (DL, LL, SDL, WX...)</summary>
        public string LoadPattern { get; set; }

        /// <summary>Giá tr? t?i (kN/m², kN/m, ho?c kN tùy lo?i)</summary>
        public double Value1 { get; set; }

        /// <summary>Giá tr? t?i cu?i (cho tr??ng h?p t?i hình thang)</summary>
        public double Value2 { get; set; }

        /// <summary>
        /// Lo?i t?i:
        /// - FrameDistributed: T?i phân b? trên Frame
        /// - FramePoint: T?i t?p trung trên Frame
        /// - AreaUniform: T?i ??u trên Area (kN/m²)
        /// - AreaUniformToFrame: T?i Area chuy?n v? Frame (1-way/2-way)
        /// - PointForce: T?i t?p trung trên Point
        /// - JointMass: Kh?i l??ng tham gia dao ??ng
        /// </summary>
        public string LoadType { get; set; }

        /// <summary>H??ng t?i (Gravity, X, Y, Z, Local 1/2/3)</summary>
        public string Direction { get; set; }

        /// <summary>Lo?i phân ph?i (1-Way, 2-Way) cho AreaToFrame</summary>
        public string DistributionType { get; set; }

        /// <summary>V? trí b?t ??u t?i (relative 0-1 ho?c absolute)</summary>
        public double DistStart { get; set; }

        /// <summary>V? trí k?t thúc t?i</summary>
        public double DistEnd { get; set; }

        /// <summary>Có s? d?ng kho?ng cách t??ng ??i không</summary>
        public bool IsRelative { get; set; }

        /// <summary>H? t?a ??</summary>
        public string CoordSys { get; set; } = "Global";

        /// <summary>Cao ?? Z c?a ph?n t? (?? xác ??nh t?ng)</summary>
        public double ElementZ { get; set; }

        /// <summary>??n v? t?i theo lo?i</summary>
        public string GetUnitString()
        {
            switch (LoadType)
            {
                case "AreaUniform":
                case "AreaUniformToFrame":
                    return "kN/m²";
                case "FrameDistributed":
                    return "kN/m";
                case "PointForce":
                case "FramePoint":
                case "JointMass":
                    return "kN";
                default:
                    return "";
            }
        }

        public override string ToString()
        {
            return $"{LoadPattern}|{ElementName}|{LoadType}|{Value1:0.00}{GetUnitString()}|{Direction}";
        }
    }

    /// <summary>
    /// Báo cáo ki?m toán t?i tr?ng hoàn ch?nh
    /// </summary>
    public class AuditReport
    {
        /// <summary>Load Pattern ???c ki?m toán</summary>
        public string LoadPattern { get; set; }

        /// <summary>Ngày gi? t?o báo cáo</summary>
        public DateTime AuditDate { get; set; }

        /// <summary>Danh sách nhóm theo t?ng</summary>
        public List<AuditStoryGroup> Stories { get; set; } = new List<AuditStoryGroup>();

        /// <summary>T?ng l?c tính toán t? CAD/Input</summary>
        public double TotalCalculatedForce => Stories.Sum(s => s.SubTotalForce);

        /// <summary>Ph?n l?c t? SAP2000 (n?u ?ã ch?y phân tích)</summary>
        public double SapBaseReaction { get; set; }

        /// <summary>Sai l?ch gi?a tính toán và ph?n l?c</summary>
        public double Difference => TotalCalculatedForce - Math.Abs(SapBaseReaction);

        /// <summary>Ph?n tr?m sai l?ch</summary>
        public double DifferencePercent => Math.Abs(SapBaseReaction) > 0.01
            ? (Difference / Math.Abs(SapBaseReaction)) * 100.0
            : 0;

        /// <summary>Tên model SAP2000</summary>
        public string ModelName { get; set; }

        /// <summary>??n v? ?ang s? d?ng</summary>
        public string UnitInfo { get; set; }
    }

    /// <summary>
    /// Nhóm t?i theo t?ng
    /// </summary>
    public class AuditStoryGroup
    {
        /// <summary>Tên t?ng</summary>
        public string StoryName { get; set; }

        /// <summary>Cao ?? t?ng (mm)</summary>
        public double Elevation { get; set; }

        /// <summary>Danh sách nhóm t?i trong t?ng</summary>
        public List<AuditLoadTypeGroup> LoadTypeGroups { get; set; } = new List<AuditLoadTypeGroup>();

        /// <summary>T?ng l?c c?a t?ng</summary>
        public double SubTotalForce => LoadTypeGroups.Sum(g => g.TotalForce);

        /// <summary>S? ph?n t? trong t?ng</summary>
        public int ElementCount => LoadTypeGroups.Sum(g => g.ElementCount);
    }

    /// <summary>
    /// Nhóm theo lo?i t?i (Area/Frame/Point)
    /// </summary>
    public class AuditLoadTypeGroup
    {
        /// <summary>Lo?i t?i (AREA UNIFORM, FRAME DISTRIBUTED, POINT LOAD...)</summary>
        public string LoadTypeName { get; set; }

        /// <summary>Danh sách nhóm theo giá tr?</summary>
        public List<AuditValueGroup> ValueGroups { get; set; } = new List<AuditValueGroup>();

        /// <summary>T?ng l?c c?a lo?i t?i này</summary>
        public double TotalForce => ValueGroups.Sum(g => g.TotalForce);

        /// <summary>S? ph?n t?</summary>
        public int ElementCount => ValueGroups.Sum(g => g.Entries.Count);
    }

    /// <summary>
    /// Nhóm theo giá tr? t?i (VD: t?t c? sàn có t?i 5kN/m²)
    /// </summary>
    public class AuditValueGroup
    {
        /// <summary>Giá tr? t?i</summary>
        public double LoadValue { get; set; }

        /// <summary>H??ng t?i</summary>
        public string Direction { get; set; }

        /// <summary>Danh sách m?c chi ti?t</summary>
        public List<AuditEntry> Entries { get; set; } = new List<AuditEntry>();

        /// <summary>T?ng s? l??ng (m² ho?c m)</summary>
        public double TotalQuantity => Entries.Sum(e => e.Quantity);

        /// <summary>T?ng l?c</summary>
        public double TotalForce => Entries.Sum(e => e.Force);
    }

    /// <summary>
    /// Chi ti?t m?t m?c trong báo cáo
    /// </summary>
    public class AuditEntry
    {
        /// <summary>V? trí theo tr?c (VD: "Tr?c 1-2 / A-B")</summary>
        public string GridLocation { get; set; }

        /// <summary>Công th?c di?n gi?i (VD: "5.0 x 6.0 - 1.2x2.0 (L?)")</summary>
        public string Explanation { get; set; }

        /// <summary>S? l??ng (m² ho?c m ho?c count)</summary>
        public double Quantity { get; set; }

        /// <summary>L?c (kN)</summary>
        public double Force { get; set; }

        /// <summary>Danh sách tên ph?n t? g?c</summary>
        public List<string> ElementList { get; set; } = new List<string>();

        /// <summary>S? ph?n t?</summary>
        public int ElementCount => ElementList?.Count ?? 0;
    }
}
