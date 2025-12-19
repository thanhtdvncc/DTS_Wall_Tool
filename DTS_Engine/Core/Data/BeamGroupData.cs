using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace DTS_Engine.Core.Data
{
    // ===== PHÂN ĐOẠN VẬT LÝ (SAP/CAD) =====
    /// <summary>
    /// Đại diện cho 1 đoạn dầm vật lý trong CAD hoặc SAP.
    /// Một nhịp logic (SpanData) có thể chứa nhiều PhysicalSegment.
    /// VD: Nhịp 6m có thể gồm 3 frame SAP 2m.
    /// </summary>
    public class PhysicalSegment
    {
        /// <summary>
        /// Handle của entity trong CAD
        /// </summary>
        public string EntityHandle { get; set; }

        /// <summary>
        /// Tên frame trong SAP (nếu có mapping)
        /// </summary>
        public string SapFrameName { get; set; }

        /// <summary>
        /// Chiều dài đoạn (m)
        /// </summary>
        public double Length { get; set; }

        /// <summary>
        /// Điểm đầu (X, Y) trong hệ tọa độ CAD
        /// </summary>
        public double[] StartPoint { get; set; } = new double[2];

        /// <summary>
        /// Điểm cuối (X, Y) trong hệ tọa độ CAD
        /// </summary>
        public double[] EndPoint { get; set; } = new double[2];

        // Thép bố trí (project từ parent SpanData)
        public string TopRebar { get; set; }
        public string BotRebar { get; set; }
        public string Stirrup { get; set; }
    }

    // ===== DTO CHO BAR SEGMENT (JS Viewer) =====
    /// <summary>
    /// DTO để truyền kết quả RebarCuttingAlgorithm sang JS Viewer.
    /// Dùng PascalCase để match với JSON serialization mặc định.
    /// </summary>
    public class BarSegmentDto
    {
        public double StartPos { get; set; }
        public double EndPos { get; set; }
        public double Length => EndPos - StartPos;
        public bool SpliceAtStart { get; set; }
        public bool SpliceAtEnd { get; set; }
        public double? SplicePosition { get; set; }
        public bool IsStaggered { get; set; }
        public int BarIndex { get; set; }
        public bool HookAtStart { get; set; }
        public bool HookAtEnd { get; set; }
        public int HookAngle { get; set; } = 90;
        public double HookLength { get; set; }
    }

    // ===== LOẠI GỐI ĐỠ =====
    public enum SupportType
    {
        Column,     // Cột
        Wall,       // Vách cứng
        Beam,       // Dầm chính đỡ dầm phụ
        FreeEnd     // Đầu thừa (cantilever)
    }

    // ===== GỐI ĐỠ =====
    /// <summary>
    /// Gối đỡ của dải dầm liên tục (Column, Wall, Beam, hoặc FreeEnd)
    /// </summary>
    public class SupportData
    {
        public string SupportId { get; set; }
        public int SupportIndex { get; set; }
        public SupportType Type { get; set; }

        /// <summary>
        /// Bề rộng gối theo hướng dầm (mm)
        /// </summary>
        public double Width { get; set; }

        /// <summary>
        /// Vị trí tim gối (m từ đầu dải dầm)
        /// </summary>
        public double Position { get; set; }

        /// <summary>
        /// Tên lưới trục (VD: "1", "A") hoặc tọa độ nếu không có grid
        /// </summary>
        public string GridName { get; set; }

        /// <summary>
        /// Handle của entity CAD (null nếu FreeEnd)
        /// </summary>
        public string EntityHandle { get; set; }

        /// <summary>
        /// True nếu đây là đầu thừa (cantilever)
        /// </summary>
        [JsonIgnore]
        public bool IsFreeEnd => Type == SupportType.FreeEnd;
    }

    // ===== NHỊP LOGIC =====
    /// <summary>
    /// Nhịp logic của dải dầm (từ gối đến gối).
    /// Chứa danh sách PhysicalSegment (các đoạn SAP/CAD).
    /// </summary>
    public class SpanData
    {
        public string SpanId { get; set; }
        public int SpanIndex { get; set; }

        /// <summary>
        /// Chiều dài tim-tim gối (m)
        /// </summary>
        public double Length { get; set; }

        /// <summary>
        /// Chiều dài mép-mép gối (m) = nhịp tịnh
        /// </summary>
        public double ClearLength { get; set; }

        /// <summary>
        /// Bề rộng tiết diện (mm)
        /// </summary>
        public double Width { get; set; }

        /// <summary>
        /// Chiều cao tiết diện (mm)
        /// </summary>
        public double Height { get; set; }

        public string LeftSupportId { get; set; }
        public string RightSupportId { get; set; }

        // ===== SUB-SEGMENTS (SAP mapping) =====
        /// <summary>
        /// Các đoạn vật lý trong nhịp này.
        /// Khi user nhập thép cho SpanData → Auto project xuống segments.
        /// </summary>
        public List<PhysicalSegment> Segments { get; set; } = new List<PhysicalSegment>();

        // ===== STEP CHANGE (giật cấp) =====
        /// <summary>
        /// True nếu chiều cao nhịp này khác nhịp trước > 50mm
        /// </summary>
        public bool IsStepChange { get; set; } = false;

        /// <summary>
        /// Chênh lệch chiều cao so với nhịp trước (mm)
        /// </summary>
        public double HeightDifference { get; set; } = 0;

        // ===== CONSOLE (đầu thừa) =====
        /// <summary>
        /// True nếu một đầu là FreeEnd (cantilever)
        /// </summary>
        public bool IsConsole { get; set; } = false;

        // ===== DIỆN TÍCH YÊU CẦU (6 vị trí) =====
        // Index: 0=GốiT, 1=L/4T, 2=Giữa, 3=L/4P, 4=GốiP, 5=Reserve
        public double[] As_Top { get; set; } = new double[6];
        public double[] As_Bot { get; set; } = new double[6];

        // ===== KẾT QUẢ BỐ THÉP (3 lớp × 6 vị trí) =====
        // [layer, position] - layer: 0-2, position: 0-5
        public string[,] TopRebar { get; set; } = new string[3, 6];
        public string[,] BotRebar { get; set; } = new string[3, 6];

        // ===== THÉP ĐAI & BỤNG =====
        // Index: 0=Đầu, 1=Giữa, 2=Cuối
        public string[] Stirrup { get; set; } = new string[3];
        public string SideBar { get; set; }

        // ===== SPLICE POSITIONS (cho tương lai) =====
        /// <summary>
        /// Vị trí nối thép top (% từ đầu nhịp). Rỗng = không nối trong nhịp này.
        /// </summary>
        public List<double> SplicePositions_Top { get; set; } = new List<double>();
        public List<double> SplicePositions_Bot { get; set; } = new List<double>();

        /// <summary>
        /// Nhịp này có active (nhận thay đổi hàng loạt) không?
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Flag đánh dấu nhịp đã được chỉnh sửa thủ công (thép, nối, cắt).
        /// Khi Import SAP lại, chỉ cập nhật nội lực, KHÔNG chạy RebarCalculator đè lên.
        /// </summary>
        public bool IsManualModified { get; set; } = false;

        /// <summary>
        /// Thời điểm sửa thủ công gần nhất (UTC)
        /// </summary>
        public DateTime? LastManualEdit { get; set; }
    }

    // ===== NHÓM DẦM LIÊN TỤC =====
    /// <summary>
    /// Nhóm dầm liên tục (Girder hoặc Beam chạy qua nhiều cột).
    /// Chứa danh sách SpanData và SupportData.
    /// </summary>
    public class BeamGroup
    {
        /// <summary>
        /// Tên nhóm theo quy tắc: GX-B x (1-11), BY-B(-2.5) x (2-5)
        /// </summary>
        public string GroupName { get; set; }

        /// <summary>
        /// Loại: "Girder" hoặc "Beam"
        /// </summary>
        public string GroupType { get; set; }

        /// <summary>
        /// Tên trục chính (VD: "B", "C", "3")
        /// </summary>
        public string AxisName { get; set; }

        /// <summary>
        /// Offset so với trục (m). VD: -2.5 nếu cách trục B 2.5m
        /// </summary>
        public double AxisOffset { get; set; }

        /// <summary>
        /// Phạm vi lưới (VD: "1-11", "2-5")
        /// </summary>
        public string GridRange { get; set; }

        /// <summary>
        /// Hướng chạy: "X" hoặc "Y"
        /// </summary>
        public string Direction { get; set; }

        // ===== MULTI-STORY SUPPORT =====
        /// <summary>
        /// Tên tầng (VD: "L1", "L2", "Roof")
        /// Dùng để phân biệt dầm cùng tên ở các tầng khác nhau
        /// </summary>
        public string StoryName { get; set; }

        /// <summary>
        /// Cao độ Z (mm) của nhóm dầm
        /// Dùng để auto-group theo cao độ
        /// </summary>
        public double LevelZ { get; set; }

        /// <summary>
        /// Bề rộng tiết diện chung (mm)
        /// </summary>
        public double Width { get; set; }

        /// <summary>
        /// Chiều cao tiết diện chung (mm)
        /// </summary>
        public double Height { get; set; }

        // ===== FLAGS =====
        public bool HasStepChange { get; set; } = false;
        public bool HasConsole { get; set; } = false;

        /// <summary>
        /// True if this is a temporary single-beam group (not from grouping, just for viewer preview)
        /// </summary>
        public bool IsSingleBeam { get; set; } = false;

        /// <summary>
        /// Tổng chiều dài dải dầm (m)
        /// </summary>
        public double TotalLength { get; set; }

        /// <summary>
        /// True nếu TotalLength > StandardBarLength (cần nối thép)
        /// </summary>
        public bool RequiresSplice { get; set; } = false;

        // ===== DATA =====
        public List<SpanData> Spans { get; set; } = new List<SpanData>();
        public List<SupportData> Supports { get; set; } = new List<SupportData>();

        // ===== BACKBONE OPTIONS (tối đa 3 phương án máy đề xuất) =====
        /// <summary>
        /// Danh sách phương án máy đề xuất (System Proposals)
        /// Option 1: Tiết kiệm (D nhỏ, nhiều thanh)
        /// Option 2: Cân bằng
        /// Option 3: Thi công nhanh (D lớn, ít thanh)
        /// </summary>
        public List<ContinuousBeamSolution> BackboneOptions { get; set; } = new List<ContinuousBeamSolution>();

        /// <summary>
        /// Index phương án đang chọn hiển thị (chưa phải chốt)
        /// </summary>
        public int SelectedBackboneIndex { get; set; } = 0;

        // ===== SELECTED DESIGN (Phương án CHỐT) =====
        /// <summary>
        /// Phương án CHỐT (User Selected/Official Design)
        /// Đây là phương án sẽ dùng để vẽ CAD và xuất thuyết minh.
        /// Khi recalculate, chỉ cập nhật BackboneOptions, KHÔNG GHI ĐÈ SelectedDesign.
        /// </summary>
        public ContinuousBeamSolution SelectedDesign { get; set; }

        /// <summary>
        /// Thời điểm chốt phương án (UTC)
        /// </summary>
        public DateTime? LockedAt { get; set; }

        /// <summary>
        /// Người chốt phương án (nếu có)
        /// </summary>
        public string LockedBy { get; set; }

        /// <summary>
        /// Kiểm tra đã chốt phương án chưa (Computed property)
        /// </summary>
        [JsonIgnore]
        public bool IsDesignLocked => SelectedDesign != null;

        // ===== METADATA =====
        /// <summary>
        /// Nguồn gốc: "Auto" (tự detect) hoặc "Manual" (user tạo)
        /// </summary>
        public string Source { get; set; } = "Auto";

        public List<string> EntityHandles { get; set; } = new List<string>();

        /// <summary>
        /// Hash của geometry để detect thay đổi (Re-hydration)
        /// </summary>
        public string GeometryHash { get; set; }

        // ===== SMART NAMING SYSTEM =====
        /// <summary>
        /// Tên Label hiển thị (VD: "B101", "G205").
        /// Dùng chung cho các dầm có cùng Signature.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// True = User đã sửa tên thủ công → Không auto đổi nữa.
        /// </summary>
        public bool IsNameLocked { get; set; } = false;

        /// <summary>
        /// "Chữ ký" nhận dạng để so sánh sự giống nhau.
        /// Format: TYPE_WxH_MAT_TOP(nxD)_BOT(nxD)
        /// VD: B_300x500_B25_T(1x18+2x20)_B(3x22)
        /// </summary>
        public string Signature { get; set; }

        /// <summary>
        /// Cập nhật chữ ký dựa trên SelectedDesign hoặc ProposedDesign[0].
        /// QUAN TRỌNG: Sắp xếp đường kính trước khi nối chuỗi để đảm bảo
        /// 2D20+1D18 == 1D18+2D20 (deterministic).
        /// </summary>
        public void UpdateSignature()
        {
            var design = this.SelectedDesign ?? (this.BackboneOptions.Count > 0 ? this.BackboneOptions[0] : null);
            if (design == null)
            {
                this.Signature = $"B_{Width}x{Height}_NoDesign";
                return;
            }

            // Lấy thông tin thép từ design (BackboneCount + Diameter)
            // Format: nDd (VD: 2D20, 3D25)
            string topInfo = $"{design.BackboneCount_Top}D{design.BackboneDiameter}";
            string botInfo = $"{design.BackboneCount_Bot}D{design.BackboneDiameter}";
            string material = "B25"; // TODO: Get from settings if needed

            // Signature = TYPE_WxH_MAT_TOP_BOT
            this.Signature = $"B_{Width}x{Height}_{material}_T({topInfo})_B({botInfo})";
        }

        /// <summary>
        /// Helper: Format thông tin thép thành chuỗi chuẩn hóa (sorted ascending).
        /// </summary>
        private string FormatRebarInfo(string rebarString)
        {
            if (string.IsNullOrEmpty(rebarString))
                return "0x0";

            // Parse và sort: "2D20+1D18" → ["1x18", "2x20"]
            var parts = rebarString.Split('+')
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p))
                .OrderBy(p => ExtractDiameter(p))
                .ToList();

            return parts.Count > 0 ? string.Join("+", parts) : "0x0";
        }

        /// <summary>
        /// Helper: Trích xuất đường kính từ chuỗi "2D20" → 20
        /// </summary>
        private int ExtractDiameter(string s)
        {
            var match = System.Text.RegularExpressions.Regex.Match(s, @"D?(\d+)");
            return match.Success && int.TryParse(match.Groups[1].Value, out int d) ? d : 0;
        }

        // ===== PRE-CALCULATED BAR SEGMENTS (từ RebarCuttingAlgorithm) =====
        /// <summary>
        /// Danh sách đoạn thép TOP đã tính toán (cắt + nối + hook)
        /// </summary>
        public List<BarSegmentDto> TopBarSegments { get; set; } = new List<BarSegmentDto>();

        /// <summary>
        /// Danh sách đoạn thép BOT đã tính toán (cắt + nối + hook)
        /// </summary>
        public List<BarSegmentDto> BotBarSegments { get; set; } = new List<BarSegmentDto>();

        // ===== MATERIAL GRADES (per-group override) =====
        /// <summary>
        /// Mác bê tông cho nhóm dầm này (mặc định theo global settings)
        /// </summary>
        public string ConcreteGrade { get; set; } = "B25";

        /// <summary>
        /// Mác thép cho nhóm dầm này  (mặc định theo global settings)
        /// </summary>
        public string SteelGrade { get; set; } = "CB400";

        /// <summary>
        /// User đã chỉnh sửa thủ công chưa?
        /// </summary>
        public bool IsManuallyEdited { get; set; } = false;
    }

    // ===== PHƯƠNG ÁN BỐ THÉP =====
    /// <summary>
    /// Kết quả bố thép cho 1 dải dầm liên tục (1 phương án backbone)
    /// </summary>
    public class ContinuousBeamSolution
    {
        public string OptionName { get; set; }

        // ===== BACKBONE (Layer 1 chạy suốt) =====
        public int BackboneDiameter { get; set; }
        public int BackboneCount_Top { get; set; }
        public int BackboneCount_Bot { get; set; }
        public double As_Backbone_Top { get; set; }
        public double As_Backbone_Bot { get; set; }

        // ===== REINFORCEMENTS =====
        /// <summary>
        /// Dictionary: "SpanId_Position" → RebarSpec
        /// VD: "S1_Top_Left" → {Diameter=22, Count=2}
        /// </summary>
        public Dictionary<string, RebarSpec> Reinforcements { get; set; } = new Dictionary<string, RebarSpec>();

        // ===== METRICS =====
        public double TotalSteelWeight { get; set; }
        public double EfficiencyScore { get; set; }
        public double ConstructabilityScore { get; set; }
        public double TotalScore { get; set; }
        public double WastePercentage { get; set; }

        /// <summary>
        /// Mô tả ngắn gọn: "2D22 suốt + gia cường D22 tại gối"
        /// </summary>
        public string Description { get; set; }

        // ===== VALIDATION (So sánh với nội lực mới) =====
        /// <summary>
        /// Phương án còn đủ khả năng chịu lực không?
        /// True = As_provided >= As_required (mới)
        /// False = Thiếu thép cần cảnh báo
        /// </summary>
        public bool IsValid { get; set; } = true;

        /// <summary>
        /// Thông báo lỗi nếu không valid
        /// VD: "As_top thiếu 2.5cm² tại gối trái S2"
        /// </summary>
        public string ValidationMessage { get; set; }

        /// <summary>
        /// As_required mới nhất (từ lần tính cuối cùng)
        /// Dùng để so sánh khi nội lực SAP thay đổi
        /// </summary>
        public double As_Required_Top_Max { get; set; }
        public double As_Required_Bot_Max { get; set; }
    }

    // ===== THÉP GIA CƯỜNG =====
    public class RebarSpec
    {
        public int Diameter { get; set; }
        public int Count { get; set; }
        public string Position { get; set; }  // "Top" hoặc "Bot"
        public int Layer { get; set; }        // 1 = chạy suốt, 2+ = gia cường

        /// <summary>
        /// Chuỗi hiển thị: "2D22"
        /// </summary>
        public string DisplayString => $"{Count}D{Diameter}";
    }
}
