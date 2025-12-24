using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        /// V3.5.2: Enable detailed pipeline logging for debugging.
        /// Logs to %LocalAppData%\DTS_Engine\Logs
        /// </summary>
        public bool EnablePipelineLogging { get; set; } = false;

        /// <summary>
        /// Cấu hình Neo & Nối thép (Anchorage & Splicing)
        /// </summary>
        public AnchorageConfig Anchorage { get; set; } = new AnchorageConfig();

        /// <summary>
        /// Cấu hình quy tắc Bố trí chi tiết (Detailing Rules)
        /// </summary>
        public DetailingConfig Detailing { get; set; } = new DetailingConfig();

        /// <summary>
        /// Cấu hình Export SAP (Section Naming)
        /// </summary>
        public ExportConfig Export { get; set; } = new ExportConfig();

        /// <summary>
        /// Cấu hình quy tắc cắt/nối thép (Curtailment Rules)
        /// </summary>
        public CurtailmentConfig Curtailment { get; set; } = new CurtailmentConfig();

        /// <summary>
        /// Cấu hình quy tắc và điểm phạt (Design Rules & Penalties)
        /// </summary>
        public RulesConfig Rules { get; set; } = new RulesConfig();

        /// <summary>
        /// Cấu hình quy tắc bố trí đai (Stirrup Configuration)
        /// Dựa trên bảng tra tiêu chuẩn theo số thanh thép thực tế
        /// </summary>
        public StirrupConfig Stirrup { get; set; } = new StirrupConfig();


        // ===== MULTI-STORY NAMING SYSTEM =====
        /// <summary>
        /// Danh sách cấu hình đặt tên theo tầng.
        /// Dùng ObjectCreationHandling.Replace để ghi đè list cũ khi deserialize.
        /// </summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<StoryNamingConfig> StoryConfigs { get; set; } = new List<StoryNamingConfig>();

        /// <summary>
        /// Dung sai cao độ (mm) để gán dầm vào tầng.
        /// Mặc định 500mm cho phép bắt dính dầm có chút lệch cao độ.
        /// </summary>
        public double StoryTolerance { get; set; } = 500;

        /// <summary>
        /// User-defined presets cho Anchorage tab.
        /// Lưu vào file thay vì localStorage để đảm bảo persistence.
        /// </summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public Dictionary<string, object> UserPresets { get; set; } = new Dictionary<string, object>();

        /// <summary>
        /// Tìm cấu hình tầng dựa trên cao độ Z của dầm.
        /// Ưu tiên Floor Below nếu dầm nằm giữa 2 tầng (cho thi công).
        /// </summary>
        public StoryNamingConfig GetStoryConfig(double z)
        {
            if (StoryConfigs == null || StoryConfigs.Count == 0)
                return null;

            // Sắp xếp theo cao độ tăng dần
            var sorted = StoryConfigs.OrderBy(s => s.Elevation).ToList();

            // Tìm tầng gần nhất trong dung sai
            StoryNamingConfig bestMatch = null;
            double minDistance = double.MaxValue;

            foreach (var config in sorted)
            {
                double distance = Math.Abs(config.Elevation - z);
                if (distance <= StoryTolerance && distance < minDistance)
                {
                    minDistance = distance;
                    bestMatch = config;
                }
            }

            // Nếu nằm giữa 2 tầng (equidistant), ưu tiên Floor Below
            // Dầm chiếu nghỉ thường đổ cùng cột vách tầng dưới
            if (bestMatch == null)
            {
                // Fallback: Tìm tầng cao nhất có Elevation <= Z
                bestMatch = sorted.LastOrDefault(s => s.Elevation <= z);
            }

            return bestMatch;
        }

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
        /// V3.5.2: Force reload settings from file.
        /// Call this before calculations to ensure UI changes are reflected.
        /// </summary>
        public static void Reload()
        {
            lock (_lock)
            {
                _instance = Load();
                System.Diagnostics.Debug.WriteLine("[DtsSettings] Reloaded settings from file.");
            }
        }

        /// <summary>
        /// Load settings từ file, tạo mới nếu không tồn tại hoặc file lỗi
        /// An toàn: Nếu file bị corrupt, tự động tạo mới và save lại
        /// </summary>
        public static DtsSettings Load()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    string json = File.ReadAllText(_settingsPath);
                    var loaded = JsonConvert.DeserializeObject<DtsSettings>(json);
                    if (loaded != null)
                    {
                        return loaded;
                    }
                    // File exists but NULL after deserialize = corrupt
                    System.Diagnostics.Debug.WriteLine("[DtsSettings] WARNING: Settings file corrupt, creating default...");
                }
            }
            catch (System.Exception ex)
            {
                // File corrupt or IO error - log and create default
                System.Diagnostics.Debug.WriteLine($"[DtsSettings] ERROR loading settings: {ex.Message}");
                System.Diagnostics.Debug.WriteLine("[DtsSettings] Creating default settings...");
            }

            // Create fresh default settings and save immediately
            var defaultSettings = new DtsSettings();
            try
            {
                defaultSettings.Save();
                System.Diagnostics.Debug.WriteLine("[DtsSettings] Default settings saved successfully.");
            }
            catch { /* Ignore save errors */ }

            return defaultSettings;
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

        /// <summary>
        /// Kiểm tra Settings có hợp lệ để tính toán không
        /// </summary>
        /// <param name="error">Thông báo lỗi nếu không hợp lệ</param>
        /// <returns>True nếu settings hợp lệ</returns>
        public bool ValidateSettings(out string error)
        {
            error = null;

            // 1. Check General
            if (General == null)
            {
                error = "GeneralConfig chưa khởi tạo";
                return false;
            }
            if (General.AvailableDiameters == null || General.AvailableDiameters.Count == 0)
            {
                error = "Chưa khai báo danh sách đường kính thép (AvailableDiameters)";
                return false;
            }

            // 2. Check Beam
            if (Beam == null)
            {
                error = "BeamConfig chưa khởi tạo";
                return false;
            }
            if (Beam.CoverTop <= 0 || Beam.CoverBot <= 0)
            {
                error = "Lớp bảo vệ (Cover) phải > 0";
                return false;
            }
            if (Beam.MaxLayers <= 0)
            {
                error = "Số lớp thép tối đa (MaxLayers) phải > 0";
                return false;
            }
            if (Beam.MinClearSpacing <= 0)
            {
                error = "Khoảng hở tịnh tối thiểu (MinClearSpacing) phải > 0";
                return false;
            }
            if (Beam.EstimatedStirrupDiameter <= 0)
            {
                error = "Đường kính đai ước tính (EstimatedStirrupDiameter) phải > 0";
                return false;
            }
            if (Beam.AggregateSize <= 0)
            {
                error = "Đường kính cốt liệu (AggregateSize) phải > 0";
                return false;
            }

            // 3. Check Anchorage (nếu cần cho tính toán neo/nối)
            if (Anchorage == null)
            {
                error = "AnchorageConfig chưa khởi tạo";
                return false;
            }

            return true;
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
        /// Dùng ObjectCreationHandling.Replace để tránh duplicate khi load.
        /// </summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
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

        /// <summary>
        /// Mác thép chủ (MPa). Mặc định CB400-V = 400
        /// User nhập trực tiếp hoặc chọn từ dropdown
        /// </summary>
        public double SteelGradeMain { get; set; } = 400;

        /// <summary>
        /// Mác thép đai (MPa). Mặc định CB300-V = 300
        /// </summary>
        public double SteelGradeStirrup { get; set; } = 300;

        /// <summary>
        /// Mác bê tông (MPa). Mặc định B25 = 25
        /// </summary>
        public double ConcreteGrade { get; set; } = 25;

        /// <summary>
        /// Tên mác thép (hiển thị). VD: "CB400-V", "AIII"
        /// </summary>
        public string SteelGradeName { get; set; } = "CB400-V";

        /// <summary>
        /// Tên mác bê tông (hiển thị). VD: "B25", "C30"
        /// </summary>
        public string ConcreteGradeName { get; set; } = "B25";

        // REMOVED: UseV3Pipeline - V3 Pipeline is dead code, V4 is sole engine
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
        /// Danh sách bước đai khả dụng (VD: 100, 150, 200)
        /// </summary>
        public List<int> StirrupSpacings { get; set; } = new List<int> { 100, 150, 200, 250 };

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
        /// Đường kính thép đai ước tính (mm) để tính toán số thanh tối đa.
        /// Dùng trong công thức: W_usable = B - 2×Cover - 2×EstimatedStirrupDiameter
        /// Options: 6, 8, 10, 12
        /// </summary>
        public int EstimatedStirrupDiameter { get; set; } = 10;

        /// <summary>
        /// Đường kính cốt liệu lớn nhất (mm).
        /// Theo tiêu chuẩn: min_spacing >= 1.33 × AggregateSize
        /// </summary>
        public int AggregateSize { get; set; } = 20;

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
        public int MaxClearSpacing { get; set; } = 300;

        /// <summary>
        /// Khoảng hở tối thiểu giữa các lớp thép (mm)
        /// </summary>
        public double MinLayerSpacing { get; set; } = 25;

        /// <summary>
        /// Số thanh tối đa mỗi lớp (giới hạn cứng)
        /// </summary>
        public int MaxBarsPerLayer { get; set; } = 8;

        /// <summary>
        /// Trọng số điểm hiệu suất trong tổng điểm
        /// </summary>
        public double EfficiencyScoreWeight { get; set; } = 0.6;

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

        // ===== NEW: CURTAILMENT SETTINGS (Cấu tạo cắt thép) =====
        /// <summary>
        /// Cấu hình cắt/nối thép cho Dầm Chính (Girder)
        /// </summary>
        public CurtailmentConfig GirderCurtailment { get; set; } = new CurtailmentConfig
        {
            TopSupportExtRatio = 0.33,  // L/3
            BotSpanCutRatio = 0.15      // 0.15L
        };

        /// <summary>
        /// Cấu hình cắt/nối thép cho Dầm Phụ (Beam)
        /// </summary>
        public CurtailmentConfig BeamCurtailment { get; set; } = new CurtailmentConfig
        {
            TopSupportExtRatio = 0.25,  // L/4
            BotSpanCutRatio = 0.20      // 0.20L
        };

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

        /// <summary>
        /// Đường kính thép ưu tiên (mm) cho tính điểm.
        /// Phương án dùng đường kính này sẽ được cộng điểm.
        /// Mặc định: 20 (D20)
        /// </summary>
        public int PreferredDiameter { get; set; } = 20;

        /// <summary>
        /// Ưu tiên thép Top và Bot cùng chẵn/lẻ để đai bao đều.
        /// Khi true, phương án có nTop%2 != nBot%2 sẽ bị trừ điểm.
        /// </summary>
        public bool PreferVerticalAlignment { get; set; } = true;

        /// <summary>
        /// Trọng lượng thép tối đa cho phép trên 1 mét dầm (kg/m).
        /// 0 = không giới hạn.
        /// </summary>
        public double MaxSteelWeightPerMeter { get; set; } = 0;

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

        // NOTE: AutoLegsRules đã xóa - dùng DtsSettings.Stirrup.GetLegCount() thay thế

        /// <summary>
        /// Chiều cao tối thiểu để đặt thép sườn (mm)
        /// </summary>
        public int WebBarMinHeight { get; set; } = 700;

        /// <summary>
        /// Hệ số tăng chiều dài neo cho thép lớp trên (vùng đổ BT >300mm)
        /// ACI/TCVN thường = 1.3
        /// </summary>
        public double TopBarFactor { get; set; } = 1.3;

        /// <summary>
        /// Có áp dụng TopBarFactor hay không
        /// </summary>
        public bool ApplyTopBarFactor { get; set; } = true;

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

        /// <summary>
        /// Hệ số mật độ thép heuristic (dùng để sinh kịch bản)
        /// Mặc định 180.0 (Width / 180 ~ số lượng thanh tối thiểu)
        /// </summary>
        public double DensityHeuristic { get; set; } = 180.0;

        // NOTE: TorsionDist_Top/Bot/Side aliases đã xóa - dùng TorsionDist_TopBar/BotBar/SideBar trực tiếp
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

        /// <summary>
        /// Cấu hình quy tắc bố trí đai cho cột
        /// Dựa trên bảng tra tiêu chuẩn theo số thanh thép và loại cột
        /// </summary>
        public ColumnStirrupConfig Stirrup { get; set; } = new ColumnStirrupConfig();
    }

    /// <summary>
    /// Tab 4: Naming - Đặt tên dầm/cột
    /// </summary>
    public class NamingConfig
    {
        // NOTE: BeamPrefix/GirderPrefix/ColumnPrefix/BeamSuffix đã xóa
        // Sử dụng StoryNamingConfig per-story settings thay thế

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

        /// <summary>
        /// Sai số để gom các dầm thẳng hàng (mm). Default 500mm.
        /// </summary>
        public double RowTolerance { get; set; } = 500.0;

        /// <summary>
        /// Chiều rộng tối thiểu để tự nhận diện là Dầm chính (Girder). Default 300mm.
        /// </summary>
        public double GirderMinWidth { get; set; } = 300.0;
    }

    /// <summary>
    /// Cấu hình quy tắc cắt/nối thép (Curtailment Rules)
    /// </summary>
    public class CurtailmentConfig
    {
        /// <summary>
        /// Hệ số vươn thép mũ gối ra nhịp (Tính từ mép gối)
        /// VD: 0.25 = L/4, 0.33 = L/3
        /// </summary>
        public double TopSupportExtRatio { get; set; } = 0.25;

        /// <summary>
        /// Điểm cắt lý thuyết thép bụng (Tính từ mép gối)
        /// VD: 0.15 = cắt bớt thép nhịp tại vị trí 0.15L
        /// </summary>
        public double BotSpanCutRatio { get; set; } = 0.15;

        /// <summary>
        /// Hệ số chiều dài thép gia cường gối (Left/Right)
        /// VD: 0.33 = 1/3 chiều dài nhịp
        /// Dùng để tính trọng lượng thép
        /// </summary>
        public double SupportReinfRatio { get; set; } = 0.33;

        /// <summary>
        /// Hệ số chiều dài thép gia cường bụng (Mid)
        /// VD: 0.8 = 80% chiều dài nhịp
        /// Dùng để tính trọng lượng thép
        /// </summary>
        public double MidSpanReinfRatio { get; set; } = 0.8;
    }

    /// <summary>
    /// Cấu hình Export SAP Section Naming
    /// Format: {BeamName}_{Section}_{RebarInfo}
    /// User có thể bật/tắt và tùy chỉnh thứ tự các thành phần
    /// </summary>
    public class ExportConfig
    {
        /// <summary>
        /// Bao gồm thông tin tiết diện (_{WxH}) trong tên section
        /// VD: _30x40
        /// </summary>
        public bool IncludeSection { get; set; } = true;

        /// <summary>
        /// Bao gồm thông tin thép (_{TopStart}_{TopEnd}_{BotStart}_{BotEnd}) trong tên section
        /// VD: _8.6_13.2_8.3_8.6
        /// </summary>
        public bool IncludeRebar { get; set; } = true;

        /// <summary>
        /// Format thứ tự hiển thị thép. Các placeholder:
        /// {TS}=TopStart, {TM}=TopMid, {TE}=TopEnd, {BS}=BotStart, {BM}=BotMid, {BE}=BotEnd
        /// Mặc định: "{TS}_{TE}_{BS}_{BE}" → 8.6_13.2_8.3_8.6
        /// VD user đổi: "{BS}_{BE}_{TS}_{TE}" → 8.3_8.6_8.6_13.2
        /// </summary>
        public string RebarFormat { get; set; } = "{TS}_{TE}_{BS}_{BE}";

        /// <summary>
        /// Số chữ số thập phân cho diện tích thép
        /// </summary>
        public int RebarDecimalPlaces { get; set; } = 1;

        /// <summary>
        /// Ký tự phân cách giữa các thành phần (mặc định "_")
        /// </summary>
        public string Separator { get; set; } = "_";

        /// <summary>
        /// Giới hạn độ dài tên section (SAP2000)
        /// SAP cũ: 31, SAP mới: 49
        /// </summary>
        public int MaxSectionNameLength { get; set; } = 49;
    }

    #region Anchorage & Detailing Config

    /// <summary>
    /// Chế độ tính toán chiều dài Neo/Nối
    /// </summary>
    public enum CalculationMode
    {
        /// <summary>Tính theo hệ số (ví dụ: 40d)</summary>
        ByFactor,
        /// <summary>Giá trị tuyệt đối (ví dụ: 600mm)</summary>
        ByLength
    }

    /// <summary>
    /// Cấu hình Neo & Nối thép (Anchorage & Splicing)
    /// Hỗ trợ đa tiêu chuẩn (TCVN, ACI, Eurocode, Standard RC)
    /// </summary>
    public class AnchorageConfig
    {
        // ===== TIÊU CHUẨN VÀ CHẾ ĐỘ =====
        public string StandardName { get; set; } = "TCVN";
        public CalculationMode Mode { get; set; } = CalculationMode.ByFactor;

        // ===== THƯ VIỆN VẬT LIỆU (Available - bên trái rổ) =====
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> AvailableConcreteGrades { get; set; } = new List<string>
            { "B15", "B20", "B25", "B30", "B35", "B40", "B45", "B50" };

        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> AvailableSteelGrades { get; set; } = new List<string>
            { "CB240", "CB300", "CB400", "CB500" };

        // ===== VẬT LIỆU DỰ ÁN (Selected - bên phải rổ) =====
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> ConcreteGrades { get; set; } = new List<string> { "B20", "B25", "B30", "B35" };

        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> SteelGrades { get; set; } = new List<string> { "CB300", "CB400" };

        // ===== BẢNG TRA CHIỀU DÀI NEO (Development Length) =====
        /// <summary>
        /// Key: "ConcreteGrade_SteelGrade", SubKey: Diameter, Value: Factor or Length
        /// </summary>
        public Dictionary<string, Dictionary<int, double>> AnchorageValues { get; set; }
            = new Dictionary<string, Dictionary<int, double>>();

        // ===== BẢNG TRA CHIỀU DÀI NỐI (Lap Splice Length) =====
        public Dictionary<string, Dictionary<int, double>> SpliceValues { get; set; }
            = new Dictionary<string, Dictionary<int, double>>();

        // ===== BẢNG TRA CHIỀU DÀI MÓC (Hook Length) =====
        /// <summary>
        /// Key: "ConcreteGrade_HookAngle", SubKey: Diameter, Value: Length (mm)
        /// </summary>
        public Dictionary<string, Dictionary<int, double>> HookValues { get; set; }
            = new Dictionary<string, Dictionary<int, double>>();

        // ===== CẤU HÌNH MÓC CHUẨN (Fallback) =====
        public double Hook90Factor { get; set; } = 12;
        public double MinHook90Length { get; set; } = 150;
        public double Hook135Factor { get; set; } = 6;
        public double Hook180Factor { get; set; } = 4;
        public double MinHookLength { get; set; } = 75;

        // ===== GIÁ TRỊ MẶC ĐỊNH (Fallback) =====
        public double DefaultAnchorageFactor { get; set; } = 40;
        public double DefaultSpliceFactor { get; set; } = 52;

        // ===== HELPER METHODS - THÊM DỮ LIỆU =====
        public void AddAnchorageLength(string concrete, string steel, int diameter, double value)
        {
            string key = $"{concrete}_{steel}";
            if (!AnchorageValues.ContainsKey(key))
                AnchorageValues[key] = new Dictionary<int, double>();
            AnchorageValues[key][diameter] = value;
        }

        public void AddSpliceLength(string concrete, string steel, int diameter, double value)
        {
            string key = $"{concrete}_{steel}";
            if (!SpliceValues.ContainsKey(key))
                SpliceValues[key] = new Dictionary<int, double>();
            SpliceValues[key][diameter] = value;
        }

        public void AddHookLength(string concrete, int hookAngle, int diameter, double value)
        {
            string key = $"{concrete}_{hookAngle}";
            if (!HookValues.ContainsKey(key))
                HookValues[key] = new Dictionary<int, double>();
            HookValues[key][diameter] = value;
        }

        // ===== HELPER METHODS - TRA CỨU =====
        public double GetAnchorageLength(int diameter, string concrete, string steel)
        {
            string key = $"{concrete}_{steel}";

            // Try diameter-specific lookup first
            if (AnchorageValues.ContainsKey(key) && AnchorageValues[key].ContainsKey(diameter))
            {
                double value = AnchorageValues[key][diameter];
                return Mode == CalculationMode.ByLength ? value : value * diameter;
            }

            // Fallback to default factor
            return diameter * DefaultAnchorageFactor;
        }

        public double GetSpliceLength(int diameter, string concrete, string steel)
        {
            string key = $"{concrete}_{steel}";

            if (SpliceValues.ContainsKey(key) && SpliceValues[key].ContainsKey(diameter))
            {
                double value = SpliceValues[key][diameter];
                return Mode == CalculationMode.ByLength ? value : value * diameter;
            }

            return diameter * DefaultSpliceFactor;
        }

        public double GetHookLength(int diameter, string concrete, int hookAngle)
        {
            string key = $"{concrete}_{hookAngle}";

            if (HookValues.ContainsKey(key) && HookValues[key].ContainsKey(diameter))
            {
                return HookValues[key][diameter];
            }

            // Fallback to factor-based calculation
            double factor = hookAngle == 90 ? Hook90Factor : (hookAngle == 135 ? Hook135Factor : Hook180Factor);
            double minLen = hookAngle == 90 ? MinHook90Length : MinHookLength;
            return Math.Max(diameter * factor, minLen);
        }

        // ===== PRESET LOADERS =====
        public static AnchorageConfig GetTCVN()
        {
            var config = new AnchorageConfig
            {
                StandardName = "TCVN 5574:2018",
                Mode = CalculationMode.ByFactor,
                AvailableConcreteGrades = new List<string> { "B15", "B20", "B25", "B30", "B35", "B40" },
                AvailableSteelGrades = new List<string> { "CB240", "CB300", "CB400", "CB500" },
                ConcreteGrades = new List<string> { "B20", "B25", "B30", "B35" },
                SteelGrades = new List<string> { "CB300", "CB400" }
            };

            // Default factors for TCVN
            foreach (var c in config.ConcreteGrades)
            {
                foreach (var s in config.SteelGrades)
                {
                    // Simplified: factor based on grade
                    double baseFactor = c == "B20" ? 45 : c == "B25" ? 40 : c == "B30" ? 35 : 30;
                    if (s == "CB400") baseFactor += 5;
                    if (s == "CB500") baseFactor += 10;

                    foreach (var d in new[] { 10, 12, 14, 16, 18, 20, 22, 25, 28, 32 })
                    {
                        config.AddAnchorageLength(c, s, d, baseFactor);
                        config.AddSpliceLength(c, s, d, baseFactor * 1.3);
                    }
                }
            }
            return config;
        }

        public static AnchorageConfig GetStandardRC()
        {
            var config = new AnchorageConfig
            {
                StandardName = "Standard RC (SD420)",
                Mode = CalculationMode.ByLength,
                AvailableConcreteGrades = new List<string> { "210", "280", "350" },
                AvailableSteelGrades = new List<string> { "SD420" },
                ConcreteGrades = new List<string> { "210", "280", "350" },
                SteelGrades = new List<string> { "SD420" }
            };

            // ========== NEO (Development Length) - Ld ==========
            // Concrete 210
            config.AddAnchorageLength("210", "SD420", 10, 420);
            config.AddAnchorageLength("210", "SD420", 13, 560);
            config.AddAnchorageLength("210", "SD420", 16, 700);
            config.AddAnchorageLength("210", "SD420", 19, 840);
            config.AddAnchorageLength("210", "SD420", 22, 1100);
            config.AddAnchorageLength("210", "SD420", 25, 1420);
            config.AddAnchorageLength("210", "SD420", 29, 1870);
            config.AddAnchorageLength("210", "SD420", 32, 2370);
            config.AddAnchorageLength("210", "SD420", 36, 2890);

            // Concrete 280
            config.AddAnchorageLength("280", "SD420", 10, 360);
            config.AddAnchorageLength("280", "SD420", 13, 490);
            config.AddAnchorageLength("280", "SD420", 16, 600);
            config.AddAnchorageLength("280", "SD420", 19, 730);
            config.AddAnchorageLength("280", "SD420", 22, 950);
            config.AddAnchorageLength("280", "SD420", 25, 1230);
            config.AddAnchorageLength("280", "SD420", 29, 1620);
            config.AddAnchorageLength("280", "SD420", 32, 2050);
            config.AddAnchorageLength("280", "SD420", 36, 2500);

            // Concrete 350
            config.AddAnchorageLength("350", "SD420", 10, 340);
            config.AddAnchorageLength("350", "SD420", 13, 450);
            config.AddAnchorageLength("350", "SD420", 16, 550);
            config.AddAnchorageLength("350", "SD420", 19, 670);
            config.AddAnchorageLength("350", "SD420", 22, 870);
            config.AddAnchorageLength("350", "SD420", 25, 1130);
            config.AddAnchorageLength("350", "SD420", 29, 1490);
            config.AddAnchorageLength("350", "SD420", 32, 1890);
            config.AddAnchorageLength("350", "SD420", 36, 2310);

            // ========== NỐI (Lap Splice) - Class B ==========
            // Concrete 210
            config.AddSpliceLength("210", "SD420", 10, 550);
            config.AddSpliceLength("210", "SD420", 13, 730);
            config.AddSpliceLength("210", "SD420", 16, 910);
            config.AddSpliceLength("210", "SD420", 19, 1100);
            config.AddSpliceLength("210", "SD420", 22, 1430);
            config.AddSpliceLength("210", "SD420", 25, 1850);
            config.AddSpliceLength("210", "SD420", 29, 2440);
            config.AddSpliceLength("210", "SD420", 32, 3090);
            config.AddSpliceLength("210", "SD420", 36, 3760);

            // Concrete 280
            config.AddSpliceLength("280", "SD420", 10, 470);
            config.AddSpliceLength("280", "SD420", 13, 640);
            config.AddSpliceLength("280", "SD420", 16, 790);
            config.AddSpliceLength("280", "SD420", 19, 950);
            config.AddSpliceLength("280", "SD420", 22, 1240);
            config.AddSpliceLength("280", "SD420", 25, 1600);
            config.AddSpliceLength("280", "SD420", 29, 2110);
            config.AddSpliceLength("280", "SD420", 32, 2670);
            config.AddSpliceLength("280", "SD420", 36, 3250);

            // Concrete 350
            config.AddSpliceLength("350", "SD420", 10, 440);
            config.AddSpliceLength("350", "SD420", 13, 590);
            config.AddSpliceLength("350", "SD420", 16, 720);
            config.AddSpliceLength("350", "SD420", 19, 870);
            config.AddSpliceLength("350", "SD420", 22, 1140);
            config.AddSpliceLength("350", "SD420", 25, 1470);
            config.AddSpliceLength("350", "SD420", 29, 1940);
            config.AddSpliceLength("350", "SD420", 32, 2460);
            config.AddSpliceLength("350", "SD420", 36, 3010);

            // ========== MÓC (Hook) - 90 degree ==========
            config.AddHookLength("210", 90, 10, 210);
            config.AddHookLength("210", 90, 13, 280);
            config.AddHookLength("210", 90, 16, 350);
            config.AddHookLength("210", 90, 19, 420);
            config.AddHookLength("210", 90, 22, 550);
            config.AddHookLength("210", 90, 25, 710);
            config.AddHookLength("210", 90, 29, 940);
            config.AddHookLength("210", 90, 32, 1190);

            config.AddHookLength("280", 90, 10, 180);
            config.AddHookLength("280", 90, 13, 240);
            config.AddHookLength("280", 90, 16, 300);
            config.AddHookLength("280", 90, 19, 360);
            config.AddHookLength("280", 90, 22, 470);
            config.AddHookLength("280", 90, 25, 610);
            config.AddHookLength("280", 90, 29, 810);
            config.AddHookLength("280", 90, 32, 1030);

            config.AddHookLength("350", 90, 10, 170);
            config.AddHookLength("350", 90, 13, 220);
            config.AddHookLength("350", 90, 16, 270);
            config.AddHookLength("350", 90, 19, 320);
            config.AddHookLength("350", 90, 22, 420);
            config.AddHookLength("350", 90, 25, 540);
            config.AddHookLength("350", 90, 29, 720);
            config.AddHookLength("350", 90, 32, 910);

            config.Hook135Factor = 6;
            config.MinHookLength = 75;

            return config;
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

    // ===== MULTI-STORY NAMING CONFIG =====
    /// <summary>
    /// Cấu hình đặt tên dầm cho từng tầng.
    /// Mỗi tầng có thể có Prefix, StartIndex và Suffix riêng.
    /// </summary>
    public class StoryNamingConfig
    {
        /// <summary>
        /// Tên tầng (lấy từ SAP hoặc user nhập). VD: "L1", "Story 2", "Roof"
        /// </summary>
        public string StoryName { get; set; }

        /// <summary>
        /// Cao độ Z của tầng (mm). VD: 3500, 7000, 10500
        /// </summary>
        public double Elevation { get; set; }

        /// <summary>
        /// Số bắt đầu đếm tên. VD: 100 → Tên sẽ là B101, B102...
        /// </summary>
        public int StartIndex { get; set; } = 1;

        /// <summary>
        /// Prefix cho dầm phụ. VD: "B" → B101
        /// </summary>
        public string BeamPrefix { get; set; } = "B";

        /// <summary>
        /// Prefix cho dầm chính (Girder). VD: "G" → G101
        /// </summary>
        public string GirderPrefix { get; set; } = "G";

        /// <summary>
        /// Prefix cho cột (Column). VD: "C" → C101
        /// </summary>
        public string ColumnPrefix { get; set; } = "C";

        /// <summary>
        /// Suffix (nếu cần). VD: "_L1" → B101_L1
        /// </summary>
        public string Suffix { get; set; } = "";
    }

    /// <summary>
    /// Cấu hình quy tắc và điểm phạt (Design Rules & Penalties)
    /// </summary>
    public class RulesConfig
    {
        /// <summary>
        /// Điểm phạt khi lãng phí thép (Waste)
        /// Mặc định: 20
        /// </summary>
        public double WastePenaltyScore { get; set; } = 20.0;

        /// <summary>
        /// Điểm phạt khi lệch pha Chẵn/Lẻ (Alignment)
        /// Mặc định: 25
        /// </summary>
        public double AlignmentPenaltyScore { get; set; } = 25.0;

        /// <summary>
        /// Điểm phạt khi vi phạm Pyramid Rule (thường là Critical, nhưng nếu cần warning)
        /// Mặc định: 100 (Critical)
        /// </summary>
        public double PyramidPenaltyScore { get; set; } = 100.0;

        /// <summary>
        /// Hệ số an toàn cho diện tích thép (Safety Factor)
        /// Mặc định: 1.0 (đủ đúng As_prov >= As_req)
        /// Khuyến nghị: 1.05 để đảm bảo an toàn hơn (+5%)
        /// </summary>
        public double SafetyFactor { get; set; } = 1.0;
    }

    // =====================================================================
    // STIRRUP CONFIGURATION (V3.3) - Lookup Tables for Bar Count
    // =====================================================================

    /// <summary>
    /// Cấu hình quy tắc bố trí đai dựa trên số thanh thép thực tế.
    /// Thay thế logic tính đai dựa trên bề rộng dầm (heuristic).
    /// </summary>
    public class StirrupConfig
    {
        /// <summary>
        /// Kích hoạt quy tắc bố trí đai nâng cao (tra bảng).
        /// Nếu false, sử dụng logic cũ dựa trên bề rộng dầm.
        /// </summary>
        public bool EnableAdvancedRules { get; set; } = true;

        /// <summary>
        /// Bảng 1: Cấu tạo khi CÓ thép gia cường lớp 1 (Mật độ cao).
        /// Dùng khi Layer 1 = Backbone + Addon xen kẽ.
        /// </summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<StirrupRuleRow> RulesWithAddon { get; set; } = GenerateDefaultAddonRules();

        /// <summary>
        /// Bảng 2: Cấu tạo khi CHỈ CÓ thép chạy suốt (Mật độ thấp).
        /// Dùng khi Layer 1 = Chỉ Backbone (thoáng).
        /// </summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<StirrupRuleRow> RulesBackboneOnly { get; set; } = GenerateDefaultBackboneRules();

        /// <summary>
        /// Tra bảng để lấy số nhánh đai dựa trên số thanh thép lớp 1.
        /// </summary>
        public int GetLegCount(int barCount, bool hasAddon)
        {
            if (!EnableAdvancedRules) return 2; // Default fallback

            var table = hasAddon ? RulesWithAddon : RulesBackboneOnly;
            var rule = table?.FirstOrDefault(r => r.BarCount == barCount);
            return rule?.TotalLegs ?? 2;
        }

        /// <summary>
        /// Tra bảng để lấy cấu tạo đai đầy đủ.
        /// </summary>
        public StirrupRuleRow GetRule(int barCount, bool hasAddon)
        {
            if (!EnableAdvancedRules) return null;

            var table = hasAddon ? RulesWithAddon : RulesBackboneOnly;
            return table?.FirstOrDefault(r => r.BarCount == barCount);
        }

        // ===== SEED DATA: Bảng 1 - Có gia cường (Mật độ cao) =====
        private static List<StirrupRuleRow> GenerateDefaultAddonRules()
        {
            return new List<StirrupRuleRow>
            {
                new StirrupRuleRow { BarCount = 1, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 2, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 3, RectangularLinks = "", CrossTies = "2", TotalLegs = 3 },
                new StirrupRuleRow { BarCount = 4, RectangularLinks = "2-3", CrossTies = "", TotalLegs = 4 },
                new StirrupRuleRow { BarCount = 5, RectangularLinks = "2-4", CrossTies = "3", TotalLegs = 5 },
                new StirrupRuleRow { BarCount = 6, RectangularLinks = "2-5; 3-4", CrossTies = "", TotalLegs = 6 },
                new StirrupRuleRow { BarCount = 7, RectangularLinks = "2-6; 3-5", CrossTies = "4", TotalLegs = 7 },
                new StirrupRuleRow { BarCount = 8, RectangularLinks = "2-7; 3-6; 4-5", CrossTies = "", TotalLegs = 8 },
                new StirrupRuleRow { BarCount = 9, RectangularLinks = "2-8; 3-7; 4-6", CrossTies = "5", TotalLegs = 9 },
                new StirrupRuleRow { BarCount = 10, RectangularLinks = "2-9; 3-8; 4-7; 5-6", CrossTies = "", TotalLegs = 10 },
                new StirrupRuleRow { BarCount = 11, RectangularLinks = "2-10; 3-9; 4-8; 5-7", CrossTies = "6", TotalLegs = 11 },
                new StirrupRuleRow { BarCount = 12, RectangularLinks = "2-11; 3-10; 4-9; 5-8; 6-7", CrossTies = "", TotalLegs = 12 },
                new StirrupRuleRow { BarCount = 13, RectangularLinks = "2-12; 3-11; 4-10; 5-9; 6-8", CrossTies = "7", TotalLegs = 13 },
                new StirrupRuleRow { BarCount = 14, RectangularLinks = "2-13; 3-12; 4-11; 5-10; 6-9; 7-8", CrossTies = "", TotalLegs = 14 },
                new StirrupRuleRow { BarCount = 15, RectangularLinks = "2-14; 3-13; 4-12; 5-11; 6-10; 7-9", CrossTies = "8", TotalLegs = 15 },
                new StirrupRuleRow { BarCount = 16, RectangularLinks = "2-15; 3-14; 4-13; 5-12; 6-11; 7-10; 8-9", CrossTies = "", TotalLegs = 16 },
                new StirrupRuleRow { BarCount = 17, RectangularLinks = "2-16; 3-15; 4-14; 5-13; 6-12; 7-11; 8-10", CrossTies = "9", TotalLegs = 17 },
                new StirrupRuleRow { BarCount = 18, RectangularLinks = "2-17; 3-16; 4-15; 5-14; 6-13; 7-12; 8-11; 9-10", CrossTies = "", TotalLegs = 18 },
                new StirrupRuleRow { BarCount = 19, RectangularLinks = "2-18; 3-17; 4-16; 5-15; 6-14; 7-13; 8-12; 9-11", CrossTies = "10", TotalLegs = 19 },
                new StirrupRuleRow { BarCount = 20, RectangularLinks = "2-19; 3-18; 4-17; 5-16; 6-15; 7-14; 8-13; 9-12; 10-11", CrossTies = "", TotalLegs = 20 }
            };
        }

        // ===== SEED DATA: Bảng 2 - Chỉ chạy suốt (Mật độ thấp) =====
        private static List<StirrupRuleRow> GenerateDefaultBackboneRules()
        {
            return new List<StirrupRuleRow>
            {
                new StirrupRuleRow { BarCount = 1, RectangularLinks = "", CrossTies = "1", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 2, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 3, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 4, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 5, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 6, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 7, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 8, RectangularLinks = "", CrossTies = "2-6; 4-8", TotalLegs = 4 },
                new StirrupRuleRow { BarCount = 9, RectangularLinks = "", CrossTies = "2-7; 4-9", TotalLegs = 4 },
                new StirrupRuleRow { BarCount = 10, RectangularLinks = "", CrossTies = "2-7; 4-9", TotalLegs = 4 },
                new StirrupRuleRow { BarCount = 11, RectangularLinks = "", CrossTies = "2-8; 5-10", TotalLegs = 4 },
                new StirrupRuleRow { BarCount = 12, RectangularLinks = "", CrossTies = "2-6; 8-12; 4-10", TotalLegs = 6 },
                new StirrupRuleRow { BarCount = 13, RectangularLinks = "", CrossTies = "2-6; 8-12; 4-10", TotalLegs = 6 },
                new StirrupRuleRow { BarCount = 14, RectangularLinks = "", CrossTies = "2-6; 9-13; 13-2; 6-9; 4-11", TotalLegs = 10 },
                new StirrupRuleRow { BarCount = 15, RectangularLinks = "", CrossTies = "2-6; 9-13; 13-2; 6-9; 4-11", TotalLegs = 10 },
                new StirrupRuleRow { BarCount = 16, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 17, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 18, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 19, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 20, RectangularLinks = "", CrossTies = "2-10; 4-18; 6-16; 8-14; 12-20", TotalLegs = 10 }
            };
        }
    }

    /// <summary>
    /// Một dòng trong bảng quy tắc đai.
    /// Mô tả cấu tạo đai cho số lượng thanh thép cụ thể.
    /// </summary>
    public class StirrupRuleRow
    {
        /// <summary>
        /// Số lượng thanh thép lớp 1 (Input - ReadOnly khi hiển thị)
        /// </summary>
        public int BarCount { get; set; }

        /// <summary>
        /// Cấu tạo đai lồng (Rectangular Links/Hoops).
        /// Format: "2-5" = đai lồng từ thanh 2 đến thanh 5.
        /// Nhiều đai: "2-5; 3-4" (phân cách bằng ;)
        /// </summary>
        public string RectangularLinks { get; set; } = "";

        /// <summary>
        /// Cấu tạo đai C (Crossties).
        /// Format: "3" = đai C móc vào thanh 3.
        /// Nhiều đai: "3; 5" (phân cách bằng ;)
        /// </summary>
        public string CrossTies { get; set; } = "";

        /// <summary>
        /// Tổng số nhánh đai (bao gồm cả đai kín và đai C).
        /// Dùng để tính nhanh mà không cần parse string.
        /// </summary>
        public int TotalLegs { get; set; } = 2;
    }

    // =====================================================================
    // COLUMN STIRRUP CONFIGURATION (V3.3) - Lookup Tables for Bar Count
    // =====================================================================

    /// <summary>
    /// Cấu hình quy tắc bố trí đai cho cột dựa trên số thanh thép thực tế.
    /// Có 2 bảng cho 2 loại cột: Rectangular (chữ nhật) và Circular (tròn).
    /// </summary>
    public class ColumnStirrupConfig
    {
        /// <summary>
        /// Kích hoạt quy tắc bố trí đai nâng cao (tra bảng).
        /// Nếu false, sử dụng logic đơn giản.
        /// </summary>
        public bool EnableAdvancedRules { get; set; } = true;

        /// <summary>
        /// Bảng 1: Cấu tạo đai cho cột hình CHỮ NHẬT.
        /// </summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<StirrupRuleRow> RectangularRules { get; set; } = GenerateDefaultRectangularRules();

        /// <summary>
        /// Bảng 2: Cấu tạo đai cho cột hình TRÒN.
        /// </summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<StirrupRuleRow> CircularRules { get; set; } = GenerateDefaultCircularRules();

        /// <summary>
        /// Tra bảng để lấy số nhánh đai dựa trên số thanh và loại cột.
        /// </summary>
        public int GetLegCount(int barCount, bool isCircular)
        {
            if (!EnableAdvancedRules) return 2;

            var table = isCircular ? CircularRules : RectangularRules;
            var rule = table?.FirstOrDefault(r => r.BarCount == barCount);
            return rule?.TotalLegs ?? 2;
        }

        /// <summary>
        /// Tra bảng để lấy cấu tạo đai đầy đủ.
        /// </summary>
        public StirrupRuleRow GetRule(int barCount, bool isCircular)
        {
            if (!EnableAdvancedRules) return null;

            var table = isCircular ? CircularRules : RectangularRules;
            return table?.FirstOrDefault(r => r.BarCount == barCount);
        }

        // ===== SEED DATA: Cột CHỮ NHẬT =====
        private static List<StirrupRuleRow> GenerateDefaultRectangularRules()
        {
            return new List<StirrupRuleRow>
            {
                new StirrupRuleRow { BarCount = 1, RectangularLinks = "", CrossTies = "1", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 2, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 3, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 4, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 5, RectangularLinks = "", CrossTies = "3", TotalLegs = 3 },
                new StirrupRuleRow { BarCount = 6, RectangularLinks = "3-4", CrossTies = "", TotalLegs = 4 },
                new StirrupRuleRow { BarCount = 7, RectangularLinks = "3-5", CrossTies = "", TotalLegs = 4 },
                new StirrupRuleRow { BarCount = 8, RectangularLinks = "3-6", CrossTies = "", TotalLegs = 4 },
                new StirrupRuleRow { BarCount = 9, RectangularLinks = "3-7", CrossTies = "5", TotalLegs = 5 },
                new StirrupRuleRow { BarCount = 10, RectangularLinks = "3-8; 5-6", CrossTies = "", TotalLegs = 6 },
                new StirrupRuleRow { BarCount = 11, RectangularLinks = "3-5; 7-9", CrossTies = "", TotalLegs = 6 },
                new StirrupRuleRow { BarCount = 12, RectangularLinks = "3-5; 8-10", CrossTies = "", TotalLegs = 6 },
                new StirrupRuleRow { BarCount = 13, RectangularLinks = "3-5; 9-11", CrossTies = "7", TotalLegs = 7 },
                new StirrupRuleRow { BarCount = 14, RectangularLinks = "3-5; 7-8; 10-12", CrossTies = "", TotalLegs = 8 },
                new StirrupRuleRow { BarCount = 15, RectangularLinks = "3-5; 7-9; 11-13", CrossTies = "", TotalLegs = 8 },
                new StirrupRuleRow { BarCount = 16, RectangularLinks = "3-5; 7-10; 12-14", CrossTies = "", TotalLegs = 8 },
                new StirrupRuleRow { BarCount = 17, RectangularLinks = "3-5; 7-11; 13-15", CrossTies = "9", TotalLegs = 9 },
                new StirrupRuleRow { BarCount = 18, RectangularLinks = "3-5; 7-12; 9-10; 14-16", CrossTies = "", TotalLegs = 10 },
                new StirrupRuleRow { BarCount = 19, RectangularLinks = "3-5; 7-9; 11-13; 15-17", CrossTies = "", TotalLegs = 10 },
                new StirrupRuleRow { BarCount = 20, RectangularLinks = "3-5; 7-9; 12-14; 16-18", CrossTies = "", TotalLegs = 10 }
            };
        }

        // ===== SEED DATA: Cột TRÒN =====
        private static List<StirrupRuleRow> GenerateDefaultCircularRules()
        {
            return new List<StirrupRuleRow>
            {
                new StirrupRuleRow { BarCount = 1, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 2, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 3, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 4, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 5, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 6, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 7, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 8, RectangularLinks = "", CrossTies = "2-6; 4-8", TotalLegs = 4 },
                new StirrupRuleRow { BarCount = 9, RectangularLinks = "", CrossTies = "2-7; 4-9", TotalLegs = 4 },
                new StirrupRuleRow { BarCount = 10, RectangularLinks = "", CrossTies = "2-7; 4-9", TotalLegs = 4 },
                new StirrupRuleRow { BarCount = 11, RectangularLinks = "", CrossTies = "2-8; 5-10", TotalLegs = 4 },
                new StirrupRuleRow { BarCount = 12, RectangularLinks = "", CrossTies = "2-6; 8-12; 4-10", TotalLegs = 6 },
                new StirrupRuleRow { BarCount = 13, RectangularLinks = "", CrossTies = "2-6; 8-12; 4-10", TotalLegs = 6 },
                new StirrupRuleRow { BarCount = 14, RectangularLinks = "", CrossTies = "2-6; 9-13; 13-2; 6-9; 4-11", TotalLegs = 10 },
                new StirrupRuleRow { BarCount = 15, RectangularLinks = "", CrossTies = "2-6; 9-13; 13-2; 6-9; 4-11", TotalLegs = 10 },
                new StirrupRuleRow { BarCount = 16, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 17, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 18, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 19, RectangularLinks = "", CrossTies = "", TotalLegs = 2 },
                new StirrupRuleRow { BarCount = 20, RectangularLinks = "", CrossTies = "2-10; 4-18; 6-16; 8-14; 12-20", TotalLegs = 10 }
            };
        }
    }
}

