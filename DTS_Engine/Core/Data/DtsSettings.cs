using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace DTS_Engine.Core.Data
{
    /// <summary>
    /// Cấu trúc Settings phân cấp theo MVC pattern.
    /// Tách biệt: General (Kho thép, Zone) / Beam / Column / Naming
    /// </summary>
    public class DtsSettings
    {
        private static DtsSettings _instance;
        private static readonly object _lock = new object();
        private static readonly string _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DTS_Engine", "DtsSettings.json");

        public GeneralConfig General { get; set; } = new GeneralConfig();
        public BeamConfig Beam { get; set; } = new BeamConfig();
        public ColumnConfig Column { get; set; } = new ColumnConfig();
        public NamingConfig Naming { get; set; } = new NamingConfig();

        /// <summary>
        /// Cấu hình Neo & Nối thép (Anchorage & Splicing)
        /// </summary>
        public AnchorageConfig Anchorage { get; set; } = new AnchorageConfig();

        /// <summary>
        /// Cấu hình quy tắc Bố trí chi tiết (Detailing Rules)
        /// </summary>
        public DetailingConfig Detailing { get; set; } = new DetailingConfig();

        /// <summary>
        /// Singleton Instance - Auto-load từ file nếu có
        /// </summary>
        public static DtsSettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = Load();
                        }
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Load settings từ file, tạo mới nếu không tồn tại
        /// </summary>
        public static DtsSettings Load()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    string json = File.ReadAllText(_settingsPath);
                    return JsonConvert.DeserializeObject<DtsSettings>(json) ?? new DtsSettings();
                }
            }
            catch { }
            return new DtsSettings();
        }

        /// <summary>
        /// Save settings ra file mặc định
        /// </summary>
        public void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(_settingsPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(_settingsPath, json);
            }
            catch { }
        }

        /// <summary>
        /// Export settings ra file tùy chọn
        /// </summary>
        public void ExportTo(string filePath)
        {
            string json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(filePath, json);
        }

        /// <summary>
        /// Import settings từ file tùy chọn
        /// </summary>
        public static DtsSettings ImportFrom(string filePath)
        {
            string json = File.ReadAllText(filePath);
            var imported = JsonConvert.DeserializeObject<DtsSettings>(json);
            if (imported != null)
            {
                _instance = imported;
            }
            return _instance;
        }
    }

    /// <summary>
    /// Tab 1: General - Kho thép chuẩn, Zone nội lực, Đơn vị
    /// </summary>
    public class GeneralConfig
    {
        /// <summary>
        /// Kho thép chuẩn của dự án (Inventory) - TCVN mặc định
        /// User có thể bỏ chọn các đường kính không sử dụng
        /// </summary>
        public List<int> AvailableDiameters { get; set; } = new List<int>
            { 6, 8, 10, 12, 14, 16, 18, 20, 22, 25, 28, 32 };

        /// <summary>
        /// Đoạn xét nội lực đầu dầm (tỉ lệ so với nhịp)
        /// VD: 0.25 = lấy Momen lớn nhất trong đoạn 1/4 nhịp đầu
        /// </summary>
        public double ZoneL1_Ratio { get; set; } = 0.25;

        /// <summary>
        /// Đoạn xét nội lực cuối dầm
        /// </summary>
        public double ZoneL2_Ratio { get; set; } = 0.25;

        /// <summary>
        /// Đơn vị hiển thị (mm, cm, m)
        /// </summary>
        public string Unit { get; set; } = "mm";

        /// <summary>
        /// Số chữ số thập phân khi làm tròn
        /// </summary>
        public int DecimalPlaces { get; set; } = 2;

        /// <summary>
        /// Chiều cao text label khi plot thép (mặc định = 1)
        /// </summary>
        public double TextHeight { get; set; } = 1;
    }

    /// <summary>
    /// Tab 2: Beam Settings - Thép dầm, Cover, Spacing, Torsion, Arrangement
    /// </summary>
    public class BeamConfig
    {
        // ===== REBAR SELECTION (Input Range Strings) =====
        /// <summary>
        /// Phạm vi thép chủ (VD: "16-25" hoặc "18, 20, 22")
        /// Sẽ được parse với GeneralConfig.AvailableDiameters
        /// </summary>
        public string MainBarRange { get; set; } = "16-25";

        /// <summary>
        /// Phạm vi thép đai
        /// </summary>
        public string StirrupBarRange { get; set; } = "8-10";

        /// <summary>
        /// Phạm vi thép hông/sườn
        /// </summary>
        public string SideBarRange { get; set; } = "12-14";

        // ===== COVER =====
        public int CoverTop { get; set; } = 25;
        public int CoverBot { get; set; } = 25;
        public int CoverSide { get; set; } = 25;

        // ===== CLEAR SPACING =====
        /// <summary>
        /// Khoảng hở tịnh tối thiểu cố định (mm) - dùng khi UseBarDiameterForSpacing = false
        /// </summary>
        public int MinClearSpacing { get; set; } = 30;

        /// <summary>
        /// Sử dụng đường kính thép để tính khoảng hở tối thiểu
        /// Theo tiêu chuẩn: min spacing >= N x max(bar diameter)
        /// </summary>
        public bool UseBarDiameterForSpacing { get; set; } = true;

        /// <summary>
        /// Hệ số nhân đường kính (N). VD: 1.0 = spacing >= 1 x d_max
        /// Theo ACI/TCVN thường yêu cầu >= 1.0d hoặc >= 1.5d
        /// </summary>
        public double BarDiameterSpacingMultiplier { get; set; } = 1.0;

        /// <summary>
        /// Khoảng hở tối đa (để xét chèn kẹp thép)
        /// </summary>
        public int MaxClearSpacing { get; set; } = 80;

        // ===== TORSION DISTRIBUTION (Hệ số phân bổ thép xoắn) =====
        /// <summary>
        /// Hệ số phân bổ thép xoắn lên thép trên (Top Bar)
        /// VD: 0.25 = 25% diện tích thép xoắn phân cho thép trên
        /// </summary>
        public double TorsionDist_TopBar { get; set; } = 0.25;

        /// <summary>
        /// Hệ số phân bổ thép xoắn lên thép dưới (Bot Bar)
        /// </summary>
        public double TorsionDist_BotBar { get; set; } = 0.25;

        /// <summary>
        /// Hệ số phân bổ thép xoắn lên thép hông (Side Bar)
        /// </summary>
        public double TorsionDist_SideBar { get; set; } = 0.50;

        // ===== AUTO ARRANGEMENT (Bố trí thép tự động) =====
        /// <summary>
        /// Số lớp thép tối đa cho phép (1, 2, 3...)
        /// </summary>
        public int MaxLayers { get; set; } = 2;

        /// <summary>
        /// Số thanh tối thiểu trong mỗi lớp
        /// </summary>
        public int MinBarsPerLayer { get; set; } = 2;

        /// <summary>
        /// Ưu tiên sử dụng cùng 1 đường kính trong tất cả các lớp
        /// </summary>
        public bool PreferSingleDiameter { get; set; } = true;

        /// <summary>
        /// Ưu tiên bố trí đối xứng
        /// </summary>
        public bool PreferSymmetric { get; set; } = true;

        /// <summary>
        /// Ưu tiên đường kính chẵn (16, 18, 20... thay vì 13, 19...)
        /// </summary>
        public bool PreferEvenDiameter { get; set; } = false;

        /// <summary>
        /// Ưu tiên ít thanh hơn (đường kính lớn) thay vì nhiều thanh nhỏ
        /// </summary>
        public bool PreferFewerBars { get; set; } = true;

        // ===== CALCULATION RULES =====
        /// <summary>
        /// Hàm lượng thép tối thiểu (μ_min)
        /// </summary>
        public double MinReinforcementRatio { get; set; } = 0.002;

        // ===== STIRRUP ADVANCED =====
        /// <summary>
        /// Cho phép số nhánh đai lẻ (3, 5...)
        /// </summary>
        public bool AllowOddLegs { get; set; } = false;

        /// <summary>
        /// Quy tắc tự động số nhánh theo bề rộng
        /// Format: "250-2 400-3 600-4" (b≤250→2 nhánh, b≤400→3 nhánh...)
        /// </summary>
        public string AutoLegsRules { get; set; } = "250-2 400-3 600-4";

        /// <summary>
        /// Chiều cao tối thiểu để đặt thép sườn (mm)
        /// </summary>
        public int WebBarMinHeight { get; set; } = 700;

        // ===== CONTINUOUS BEAM RULES (Dầm liên tục) =====
        /// <summary>
        /// Ép buộc Layer 1 dùng cùng 1 đường kính cho toàn dải dầm
        /// </summary>
        public bool ForceContinuousDiameter { get; set; } = true;

        /// <summary>
        /// Chỉ dùng 1 loại đường kính trong mỗi mặt cắt (không phối D22+D20)
        /// </summary>
        public bool SingleDiameterPerSpan { get; set; } = true;

        /// <summary>
        /// Cho phép phối 2 loại đường kính (nếu SingleDiameterPerSpan = false)
        /// </summary>
        public bool AllowDiameterMixing { get; set; } = false;

        /// <summary>
        /// Số cấp đường kính chênh lệch tối đa khi phối (1 = D25+D22, 2 = D25+D20)
        /// </summary>
        public int MaxDiameterDiff { get; set; } = 1;

        /// <summary>
        /// Khớp số thanh lớp trên giữa các nhịp (tìm mẫu số chung)
        /// </summary>
        public bool MatchTopLayerBars { get; set; } = true;

        // ===== SPLICE SETTINGS (Nối thép - cho tương lai) =====
        /// <summary>
        /// Chiều dài thép tiêu chuẩn (mm). Nếu dải dầm > giá trị này → cần nối
        /// </summary>
        public double StandardBarLength { get; set; } = 11700;

        /// <summary>
        /// Hệ số nhân đường kính cho chiều dài nối chồng (40 = 40d)
        /// </summary>
        public double LapSpliceMultiplier { get; set; } = 40;

        // ===== BACKWARD COMPATIBILITY =====
        // Các property cũ để không break code cũ
        [JsonIgnore] public double TorsionDist_Top { get => TorsionDist_TopBar; set => TorsionDist_TopBar = value; }
        [JsonIgnore] public double TorsionDist_Bot { get => TorsionDist_BotBar; set => TorsionDist_BotBar = value; }
        [JsonIgnore] public double TorsionDist_Side { get => TorsionDist_SideBar; set => TorsionDist_SideBar = value; }
    }

    /// <summary>
    /// Tab 3: Column Settings - Thép cột (Basic implementation)
    /// </summary>
    public class ColumnConfig
    {
        /// <summary>
        /// Phạm vi thép chủ cột
        /// </summary>
        public string MainBarRange { get; set; } = "16-28";

        /// <summary>
        /// Phạm vi thép đai cột
        /// </summary>
        public string StirrupBarRange { get; set; } = "8-10";

        /// <summary>
        /// Lớp bảo vệ cột (mm)
        /// </summary>
        public int Cover { get; set; } = 30;

        /// <summary>
        /// Chiều dài nối chồng (số lần đường kính)
        /// VD: 40 = 40d
        /// </summary>
        public double LapSpliceMultiplier { get; set; } = 40;

        /// <summary>
        /// Số thanh tối thiểu mỗi cạnh
        /// </summary>
        public int MinBarsPerSide { get; set; } = 2;

        /// <summary>
        /// Khoảng cách đai tối đa (mm)
        /// </summary>
        public int MaxStirrupSpacing { get; set; } = 200;
    }

    /// <summary>
    /// Tab 4: Naming - Đặt tên dầm/cột
    /// </summary>
    public class NamingConfig
    {
        public string BeamPrefix { get; set; } = "B";
        public string GirderPrefix { get; set; } = "G";
        public string ColumnPrefix { get; set; } = "C";
        public string BeamSuffix { get; set; } = "";

        /// <summary>
        /// Nhóm theo trục (A1, B2...)
        /// </summary>
        public bool GroupByAxis { get; set; } = false;

        /// <summary>
        /// Gộp các dầm cùng tiết diện & thép thành 1 tên
        /// </summary>
        public bool MergeSameSection { get; set; } = true;

        /// <summary>
        /// Tự động đổi tên khi tiết diện thay đổi
        /// </summary>
        public bool AutoRenameOnSectionChange { get; set; } = false;

        /// <summary>
        /// Góc bắt đầu quét: 0=TL, 1=TR, 2=BL, 3=BR
        /// </summary>
        public int SortCorner { get; set; } = 0;

        /// <summary>
        /// Hướng quét: 0=Horizontal(X), 1=Vertical(Y)
        /// </summary>
        public int SortDirection { get; set; } = 0;
    }

    #region Anchorage & Detailing Config

    /// <summary>
    /// Cấu hình Neo & Nối thép (Anchorage & Splicing)
    /// Tách biệt quy tắc tính toán và quy tắc bố trí
    /// </summary>
    public class AnchorageConfig
    {
        // --- Vật liệu ---
        /// <summary>Mác bê tông (B20, B25, B30...)</summary>
        public string ConcreteGrade { get; set; } = "B25";

        /// <summary>Mác thép (CB300V, CB400V, CB500V)</summary>
        public string SteelGrade { get; set; } = "CB400V";

        // --- Chế độ tính toán ---
        /// <summary>True: Dùng hệ số nhân nhanh. False: Dùng bảng tra chi tiết</summary>
        public bool UseSimplifiedRules { get; set; } = true;

        // --- Quick Settings (Simplified Mode) ---
        /// <summary>Hệ số nối thép vùng kéo (mặc định 40d)</summary>
        public double TensileSpliceFactor { get; set; } = 40;

        /// <summary>Hệ số nối thép vùng nén (mặc định 30d)</summary>
        public double CompressiveSpliceFactor { get; set; } = 30;

        /// <summary>Hệ số neo thép (mặc định 35d)</summary>
        public double AnchorageFactor { get; set; } = 35;

        // --- Standard Hooks ---
        /// <summary>Hệ số móc 90° (12d)</summary>
        public double Hook90Factor { get; set; } = 12;

        /// <summary>Hệ số móc 135° cho đai (6d)</summary>
        public double Hook135Factor { get; set; } = 6;

        /// <summary>Hệ số móc 180° (4d)</summary>
        public double Hook180Factor { get; set; } = 4;

        /// <summary>Chiều dài móc tối thiểu (mm)</summary>
        public double MinHookLength { get; set; } = 75;

        // --- Manual Table (Advanced Mode) ---
        /// <summary>Bảng tra chiều dài neo theo đường kính (Key: diameter, Value: mm)</summary>
        public Dictionary<int, double> ManualDevelopmentLengths { get; set; } = new Dictionary<int, double>();

        /// <summary>Bảng tra chiều dài nối theo đường kính (Key: diameter, Value: mm)</summary>
        public Dictionary<int, double> ManualSpliceLengths { get; set; } = new Dictionary<int, double>();

        /// <summary>
        /// Tính chiều dài nối thép (Lap Splice Length) theo đường kính
        /// </summary>
        public double GetSpliceLength(int diameter, bool isTensionZone = true)
        {
            if (!UseSimplifiedRules && ManualSpliceLengths.ContainsKey(diameter))
                return ManualSpliceLengths[diameter];

            double factor = isTensionZone ? TensileSpliceFactor : CompressiveSpliceFactor;
            return diameter * factor;
        }

        /// <summary>
        /// Tính chiều dài neo thép (Anchorage Length) theo đường kính
        /// </summary>
        public double GetAnchorageLength(int diameter)
        {
            if (!UseSimplifiedRules && ManualDevelopmentLengths.ContainsKey(diameter))
                return ManualDevelopmentLengths[diameter];

            return diameter * AnchorageFactor;
        }
    }

    /// <summary>
    /// Cấu hình quy tắc Bố trí chi tiết (Detailing Rules)
    /// Phân biệt Dầm phụ (Beam) và Dầm chính (Girder)
    /// </summary>
    public class DetailingConfig
    {
        // --- General Constraints ---
        /// <summary>Chiều dài thanh thép tối đa (mm)</summary>
        public double MaxBarLength { get; set; } = 11700;

        /// <summary>Chiều dài thanh thép tối thiểu (mm) - tránh thép vụn</summary>
        public double MinBarLength { get; set; } = 2000;

        /// <summary>Khoảng cách so le mối nối tối thiểu (mm)</summary>
        public double MinStaggerDistance { get; set; } = 600;

        /// <summary>Hệ số StaggerDistance theo Ld (nếu > MinStagger thì dùng)</summary>
        public double StaggerFactorLd { get; set; } = 1.3;

        /// <summary>
        /// Bỏ qua nối thép (cho nhà cung cấp có dịch vụ cắt thép theo yêu cầu).
        /// Khi true: Vẽ thanh dài suốt, không vẽ điểm nối.
        /// </summary>
        public bool SkipSplice { get; set; } = false;

        // --- Beam Rules (Dầm phụ) ---
        public ArrangementRule BeamRule { get; set; } = new ArrangementRule
        {
            TopSpliceZone = SpliceZone.MidSpan,      // Thép trên nối giữa nhịp
            BotSpliceZone = SpliceZone.Support,      // Thép dưới nối tại gối
            AllowLapSpliceInJoint = false,           // Không cho nối trong cột
            SupportZoneRatio = 0.25                  // Vùng gối = L/4
        };

        // --- Girder Rules (Dầm chính) ---
        public ArrangementRule GirderRule { get; set; } = new ArrangementRule
        {
            TopSpliceZone = SpliceZone.QuarterSpan,  // Dầm chính nối L/4
            BotSpliceZone = SpliceZone.Support,
            AllowLapSpliceInJoint = true,            // Cho phép neo xuyên cột
            SupportZoneRatio = 0.25
        };

        /// <summary>
        /// Lấy rule phù hợp theo loại cấu kiện
        /// </summary>
        public ArrangementRule GetRule(string groupType)
        {
            return groupType?.ToUpperInvariant() == "GIRDER" ? GirderRule : BeamRule;
        }
    }

    /// <summary>
    /// Quy tắc bố trí cho một loại cấu kiện
    /// </summary>
    public class ArrangementRule
    {
        /// <summary>Vùng nối thép lớp trên</summary>
        public SpliceZone TopSpliceZone { get; set; } = SpliceZone.MidSpan;

        /// <summary>Vùng nối thép lớp dưới</summary>
        public SpliceZone BotSpliceZone { get; set; } = SpliceZone.Support;

        /// <summary>Cho phép nối thép trong nút khung</summary>
        public bool AllowLapSpliceInJoint { get; set; } = false;

        /// <summary>Tỷ lệ vùng gối so với nhịp (L/4 = 0.25)</summary>
        public double SupportZoneRatio { get; set; } = 0.25;

        /// <summary>
        /// Tính vùng cho phép nối theo SpliceZone
        /// Returns (startRatio, endRatio) relative to span length
        /// </summary>
        public (double Start, double End) GetAllowedZone(SpliceZone zone)
        {
            switch (zone)
            {
                case SpliceZone.Support:
                    return (0, SupportZoneRatio);
                case SpliceZone.QuarterSpan:
                    return (SupportZoneRatio, 1 - SupportZoneRatio);
                case SpliceZone.MidSpan:
                    return (0.35, 0.65);
                default:
                    return (0, 1);
            }
        }
    }

    /// <summary>
    /// Vùng cho phép nối thép
    /// </summary>
    public enum SpliceZone
    {
        /// <summary>Tại gối (đầu nhịp)</summary>
        Support = 0,

        /// <summary>Vùng L/4 (1/4 nhịp)</summary>
        QuarterSpan = 1,

        /// <summary>Giữa nhịp</summary>
        MidSpan = 2
    }

    #endregion
}
