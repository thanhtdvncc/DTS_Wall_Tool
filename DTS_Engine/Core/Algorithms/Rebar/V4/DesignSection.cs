using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Engine.Core.Algorithms.Rebar.V4
{
    /// <summary>
    /// Vị trí cốt thép trong mặt cắt.
    /// </summary>
    public enum RebarPosition
    {
        Top = 0,
        Bot = 1
    }

    /// <summary>
    /// Loại mặt cắt trong dầm.
    /// </summary>
    public enum SectionType
    {
        Support = 0,
        MidSpan = 1,
        FreeEnd = 2,
        Quarter = 3  // Renamed from QuarterSpan for consistency
    }

    /// <summary>
    /// Cấu hình số sections per span (linh hoạt cho N zones).
    /// </summary>
    public class DiscretizationConfig
    {
        /// <summary>Số zones per span (mặc định 3: Start, Mid, End)</summary>
        public int ZonesPerSpan { get; set; } = 3;

        /// <summary>Vị trí tương đối của các zones [0..1]</summary>
        public List<double> ZonePositions { get; set; } = new List<double> { 0, 0.5, 1.0 };

        /// <summary>Loại section tương ứng với mỗi zone</summary>
        public List<SectionType> ZoneTypes { get; set; } = new List<SectionType>
        {
            SectionType.Support,
            SectionType.MidSpan,
            SectionType.Support
        };

        /// <summary>
        /// Cấu hình mặc định: 3 zones (Support-Mid-Support)
        /// </summary>
        public static DiscretizationConfig Default => new DiscretizationConfig();

        /// <summary>
        /// Cấu hình chi tiết: 5 zones (Support-Quarter-Mid-Quarter-Support)
        /// </summary>
        public static DiscretizationConfig Detailed => new DiscretizationConfig
        {
            ZonesPerSpan = 5,
            ZonePositions = new List<double> { 0, 0.25, 0.5, 0.75, 1.0 },
            ZoneTypes = new List<SectionType>
            {
                SectionType.Support,
                SectionType.Quarter,  // Fixed: was QuarterSpan
                SectionType.MidSpan,
                SectionType.Quarter,  // Fixed: was QuarterSpan
                SectionType.Support
            }
        };
    }

    /// <summary>
    /// Mặt cắt thiết kế rời rạc - Đơn vị cơ bản của hệ thống V4.
    /// Mỗi mặt cắt chứa yêu cầu thép và danh sách phương án bố trí hợp lệ.
    /// Hỗ trợ N spans và N layers linh hoạt.
    /// ISO 12207: Analysis Phase - Discrete problem decomposition.
    /// </summary>
    public class DesignSection
    {
        #region Identity & Location

        /// <summary>Index toàn cục trong dải dầm (0..N-1)</summary>
        public int GlobalIndex { get; set; }

        /// <summary>ID mặt cắt (VD: "S1_Support_Left", "S2_MidSpan")</summary>
        public string SectionId { get; set; }

        /// <summary>Nhịp chứa mặt cắt này (0-based)</summary>
        public int SpanIndex { get; set; }

        /// <summary>Zone index trong nhịp (0-based)</summary>
        public int ZoneIndex { get; set; }

        /// <summary>ID nhịp (VD: "S1", "S2")</summary>
        public string SpanId { get; set; }

        /// <summary>Loại mặt cắt theo Topology</summary>
        public SectionType Type { get; set; }

        /// <summary>Vị trí dọc theo dầm (m từ đầu)</summary>
        public double Position { get; set; }

        /// <summary>Vị trí tương đối trong nhịp [0..1]</summary>
        public double RelativePosition { get; set; }

        #endregion

        #region Geometry

        /// <summary>Bề rộng tiết diện (mm)</summary>
        public double Width { get; set; }

        /// <summary>Chiều cao tiết diện (mm)</summary>
        public double Height { get; set; }

        /// <summary>Lớp bảo vệ trên (mm)</summary>
        public double CoverTop { get; set; } = 35;

        /// <summary>Lớp bảo vệ dưới (mm)</summary>
        public double CoverBot { get; set; } = 35;

        /// <summary>Lớp bảo vệ bên (mm)</summary>
        public double CoverSide { get; set; } = 25;

        /// <summary>Đường kính đai ước tính (mm)</summary>
        public double StirrupDiameter { get; set; } = 10;

        #endregion

        #region Requirements (Input từ SAP)

        /// <summary>Diện tích thép yêu cầu Top (cm²)</summary>
        public double ReqTop { get; set; }

        /// <summary>Diện tích thép yêu cầu Bot (cm²)</summary>
        public double ReqBot { get; set; }

        /// <summary>Diện tích đai yêu cầu (cm²/cm)</summary>
        public double ReqStirrup { get; set; }

        /// <summary>Diện tích thép web yêu cầu (cm²)</summary>
        public double ReqWeb { get; set; }

        #endregion

        #region Topology Links (Type 3 Constraint)

        /// <summary>Mặt cắt liền kề (Type 3 pairs)</summary>
        public DesignSection LinkedSection { get; set; }

        /// <summary>True nếu là gối bên trái của nhịp (kết nối với nhịp trước)</summary>
        public bool IsSupportLeft { get; set; }

        /// <summary>True nếu là gối bên phải của nhịp (kết nối với nhịp sau)</summary>
        public bool IsSupportRight { get; set; }

        #endregion

        #region Output (Computed by SectionSolver)

        /// <summary>Danh sách phương án bố trí Top hợp lệ</summary>
        public List<SectionArrangement> ValidArrangementsTop { get; set; } = new List<SectionArrangement>();

        /// <summary>Danh sách phương án bố trí Bot hợp lệ</summary>
        public List<SectionArrangement> ValidArrangementsBot { get; set; } = new List<SectionArrangement>();

        /// <summary>Phương án đã chọn cho Top (sau Synthesis)</summary>
        public SectionArrangement SelectedTop { get; set; }

        /// <summary>Phương án đã chọn cho Bot (sau Synthesis)</summary>
        public SectionArrangement SelectedBot { get; set; }

        #endregion

        #region Computed Properties

        /// <summary>Bề rộng khả dụng để bố trí thép (mm)</summary>
        public double UsableWidth => Width - 2 * CoverSide - 2 * StirrupDiameter;

        /// <summary>Chiều cao khả dụng (mm)</summary>
        public double UsableHeight => Height - CoverTop - CoverBot - 2 * StirrupDiameter;

        /// <summary>Có phải mặt cắt gối (cần đồng bộ) không?</summary>
        public bool IsSupport => Type == SectionType.Support;

        /// <summary>Có phải đầu tự do không?</summary>
        public bool IsFreeEnd => Type == SectionType.FreeEnd;

        /// <summary>Có yêu cầu thép Top không?</summary>
        public bool RequiresTopRebar => ReqTop > 0.01;

        /// <summary>Có yêu cầu thép Bot không?</summary>
        public bool RequiresBotRebar => ReqBot > 0.01;

        #endregion

        #region Methods

        /// <summary>
        /// Tạo bản sao độc lập (deep clone).
        /// </summary>
        public DesignSection Clone()
        {
            return new DesignSection
            {
                GlobalIndex = GlobalIndex,
                SectionId = SectionId,
                SpanIndex = SpanIndex,
                ZoneIndex = ZoneIndex,
                SpanId = SpanId,
                Type = Type,
                Position = Position,
                RelativePosition = RelativePosition,
                Width = Width,
                Height = Height,
                CoverTop = CoverTop,
                CoverBot = CoverBot,
                CoverSide = CoverSide,
                StirrupDiameter = StirrupDiameter,
                ReqTop = ReqTop,
                ReqBot = ReqBot,
                ReqStirrup = ReqStirrup,
                ReqWeb = ReqWeb,
                IsSupportLeft = IsSupportLeft,
                IsSupportRight = IsSupportRight,
                ValidArrangementsTop = new List<SectionArrangement>(ValidArrangementsTop),
                ValidArrangementsBot = new List<SectionArrangement>(ValidArrangementsBot)
            };
        }

        public override string ToString()
        {
            return $"{SectionId} | Type={Type} | Req: Top={ReqTop:F2}, Bot={ReqBot:F2} | Options: T={ValidArrangementsTop.Count}, B={ValidArrangementsBot.Count}";
        }

        #endregion
    }

    /// <summary>
    /// Phương án bố trí thép tại một mặt cắt.
    /// Hỗ trợ N lớp linh hoạt, mixed diameters.
    /// ISO 25010: Functional Correctness - Immutable value object.
    /// </summary>
    public class SectionArrangement : IEquatable<SectionArrangement>
    {
        #region Core Data

        /// <summary>Tổng số thanh</summary>
        public int TotalCount { get; set; }

        /// <summary>Tổng diện tích cung cấp (cm²)</summary>
        public double TotalArea { get; set; }

        /// <summary>Số lớp sử dụng (N layers, không giới hạn)</summary>
        public int LayerCount { get; set; } = 1;

        /// <summary>Số thanh mỗi lớp [Layer0, Layer1, ...] - Dynamic N layers</summary>
        public List<int> BarsPerLayer { get; set; } = new List<int>();

        /// <summary>Đường kính mỗi lớp (nếu khác nhau) [Layer0Dia, Layer1Dia, ...]</summary>
        public List<int> DiametersPerLayer { get; set; } = new List<int>();

        /// <summary>Đường kính chính (mm) - Dùng khi tất cả cùng đường kính</summary>
        public int PrimaryDiameter { get; set; }

        /// <summary>
        /// Chi tiết từng thanh (nếu mixed diameter trong cùng 1 lớp).
        /// Empty = tất cả cùng PrimaryDiameter.
        /// </summary>
        public List<int> BarDiameters { get; set; } = new List<int>();

        #endregion

        #region Flags & Constraints

        /// <summary>Tất cả thanh cùng đường kính?</summary>
        public bool IsSingleDiameter => BarDiameters.Count == 0 || BarDiameters.Distinct().Count() <= 1;

        /// <summary>Số thanh chẵn? (Computed property - không set trực tiếp)</summary>
        public bool IsEvenCount => TotalCount % 2 == 0;

        /// <summary>Đối xứng? (Cho Top-Bot compatibility check)</summary>
        public bool IsSymmetric { get; set; } = true;

        /// <summary>Phù hợp với layout đai?</summary>
        public bool FitsStirrupLayout { get; set; } = true;

        /// <summary>Khoảng hở giữa các thanh lớp 1 (mm)</summary>
        public double ClearSpacing { get; set; }

        /// <summary>Khoảng hở giữa các lớp (mm)</summary>
        public double VerticalSpacing { get; set; } = 25;

        #endregion

        #region Scoring

        /// <summary>
        /// Điểm đánh giá (0-100). Cao hơn = tốt hơn.
        /// Components: Efficiency + Constructability + Waste Penalty
        /// </summary>
        public double Score { get; set; }

        /// <summary>Số thanh lãng phí do bump (VD: 1→2)</summary>
        public int WasteCount { get; set; }

        /// <summary>Hiệu suất sử dụng thép (As_prov / As_req)</summary>
        public double Efficiency { get; set; }

        #endregion

        #region Methods

        /// <summary>
        /// Tạo chuỗi hiển thị linh hoạt cho N layers.
        /// VD: "3D20" hoặc "2D20+2D18" hoặc "4D20 (2L)"
        /// </summary>
        public string ToDisplayString()
        {
            if (TotalCount == 0) return "-";

            if (IsSingleDiameter)
            {
                if (LayerCount <= 1)
                {
                    return $"{TotalCount}D{PrimaryDiameter}";
                }
                else
                {
                    // Multiple layers, same diameter
                    var layerStr = string.Join("+", BarsPerLayer.Select(c => $"{c}D{PrimaryDiameter}"));
                    return layerStr;
                }
            }

            // Mixed diameters: Group by diameter
            if (BarDiameters.Count > 0)
            {
                var groups = BarDiameters
                    .GroupBy(d => d)
                    .OrderByDescending(g => g.Key)
                    .Select(g => $"{g.Count()}D{g.Key}");
                return string.Join("+", groups);
            }

            // Multiple layers with different diameters per layer
            if (DiametersPerLayer.Count == BarsPerLayer.Count)
            {
                var parts = new List<string>();
                for (int i = 0; i < LayerCount; i++)
                {
                    parts.Add($"{BarsPerLayer[i]}D{DiametersPerLayer[i]}");
                }
                return string.Join("+", parts);
            }

            return $"{TotalCount}D{PrimaryDiameter}";
        }

        /// <summary>
        /// Kiểm tra phương án này có "chứa" được backbone không.
        /// VD: Arrangement "3D20" chứa Backbone "2D20".
        /// </summary>
        public bool ContainsBackbone(int backboneCount, int backboneDiameter)
        {
            if (IsSingleDiameter)
            {
                return PrimaryDiameter == backboneDiameter && TotalCount >= backboneCount;
            }

            // Mixed: đếm số thanh cùng đường kính
            int matchingBars = BarDiameters.Count(d => d == backboneDiameter);
            return matchingBars >= backboneCount;
        }

        /// <summary>
        /// Tính addon (phần dư sau khi trừ backbone).
        /// </summary>
        public (int count, int diameter, double area, List<int> layerBreakdown) GetAddon(int backboneCount, int backboneDiameter)
        {
            if (!ContainsBackbone(backboneCount, backboneDiameter))
                return (0, 0, 0, new List<int>());

            if (IsSingleDiameter && PrimaryDiameter == backboneDiameter)
            {
                int addon = TotalCount - backboneCount;
                double addonArea = addon * Math.PI * backboneDiameter * backboneDiameter / 400.0;

                // Calculate layer breakdown for addon
                var addonLayers = CalculateAddonLayerBreakdown(backboneCount, addon);

                return (addon, backboneDiameter, addonArea, addonLayers);
            }

            // Mixed: Remove backbone bars first, remaining is addon
            var remaining = new List<int>(BarDiameters);
            int removed = 0;
            for (int i = 0; i < remaining.Count && removed < backboneCount; i++)
            {
                if (remaining[i] == backboneDiameter)
                {
                    remaining.RemoveAt(i);
                    i--;
                    removed++;
                }
            }

            if (remaining.Count == 0)
                return (0, 0, 0, new List<int>());

            int addonCount = remaining.Count;
            int addonDia = remaining.GroupBy(d => d).OrderByDescending(g => g.Count()).First().Key;
            double area = remaining.Sum(d => Math.PI * d * d / 400.0);

            return (addonCount, addonDia, area, new List<int> { addonCount });
        }

        /// <summary>
        /// Tính layer breakdown cho addon bars.
        /// </summary>
        private List<int> CalculateAddonLayerBreakdown(int backboneCount, int addonCount)
        {
            if (addonCount <= 0) return new List<int>();
            if (BarsPerLayer.Count == 0) return new List<int> { addonCount };

            var result = new List<int>();
            int remaining = addonCount;
            int backboneRemaining = backboneCount;

            for (int i = 0; i < BarsPerLayer.Count && remaining > 0; i++)
            {
                int layerBars = BarsPerLayer[i];

                // Subtract backbone from this layer first
                int backboneInLayer = Math.Min(backboneRemaining, layerBars);
                backboneRemaining -= backboneInLayer;

                int addonInLayer = layerBars - backboneInLayer;
                if (addonInLayer > 0)
                {
                    result.Add(addonInLayer);
                    remaining -= addonInLayer;
                }
            }

            // If still remaining, add as new layer
            if (remaining > 0)
            {
                result.Add(remaining);
            }

            return result;
        }

        /// <summary>
        /// Lấy thông tin cho N layers.
        /// </summary>
        public List<(int count, int diameter)> GetLayerDetails()
        {
            var details = new List<(int count, int diameter)>();

            if (BarsPerLayer.Count == 0)
            {
                details.Add((TotalCount, PrimaryDiameter));
                return details;
            }

            for (int i = 0; i < LayerCount; i++)
            {
                int count = i < BarsPerLayer.Count ? BarsPerLayer[i] : 0;
                int dia = DiametersPerLayer.Count > i ? DiametersPerLayer[i] : PrimaryDiameter;
                details.Add((count, dia));
            }

            return details;
        }

        #endregion

        #region Equality

        public bool Equals(SectionArrangement other)
        {
            if (other == null) return false;
            if (TotalCount != other.TotalCount) return false;
            if (PrimaryDiameter != other.PrimaryDiameter) return false;
            if (LayerCount != other.LayerCount) return false;

            // Compare bar details if mixed
            if (BarDiameters.Count != other.BarDiameters.Count) return false;
            if (BarDiameters.Count > 0)
            {
                var sorted1 = BarDiameters.OrderBy(d => d).ToList();
                var sorted2 = other.BarDiameters.OrderBy(d => d).ToList();
                for (int i = 0; i < sorted1.Count; i++)
                {
                    if (sorted1[i] != sorted2[i]) return false;
                }
            }

            return true;
        }

        public override bool Equals(object obj) => Equals(obj as SectionArrangement);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + TotalCount;
                hash = hash * 31 + PrimaryDiameter;
                hash = hash * 31 + LayerCount;
                foreach (var d in BarDiameters.OrderBy(x => x))
                    hash = hash * 31 + d;
                return hash;
            }
        }

        public override string ToString() => $"{ToDisplayString()} (L{LayerCount}, Score={Score:F1})";

        #endregion

        #region Static Factories

        /// <summary>
        /// Tạo arrangement rỗng (không yêu cầu thép).
        /// </summary>
        public static SectionArrangement Empty => new SectionArrangement
        {
            TotalCount = 0,
            TotalArea = 0,
            PrimaryDiameter = 0,
            LayerCount = 0,
            BarsPerLayer = new List<int>(),
            Score = 100,
            ClearSpacing = 0
        };

        #endregion
    }

    /// <summary>
    /// Ứng viên Backbone toàn cục.
    /// ISO 25010: Usability - Clear candidate representation.
    /// </summary>
    public class BackboneCandidate
    {
        /// <summary>Số thanh backbone Top</summary>
        public int CountTop { get; set; }

        /// <summary>Số thanh backbone Bot</summary>
        public int CountBot { get; set; }

        /// <summary>Đường kính backbone (mm)</summary>
        public int Diameter { get; set; }

        /// <summary>Diện tích backbone Top (cm²)</summary>
        public double AreaTop => CountTop * Math.PI * Diameter * Diameter / 400.0;

        /// <summary>Diện tích backbone Bot (cm²)</summary>
        public double AreaBot => CountBot * Math.PI * Diameter * Diameter / 400.0;

        /// <summary>True nếu backbone fit được tất cả sections</summary>
        public bool IsGloballyValid { get; set; }

        /// <summary>Điểm tổng hợp (Weight + Constructability)</summary>
        public double TotalScore { get; set; }

        /// <summary>Tổng trọng lượng ước tính (kg)</summary>
        public double EstimatedWeight { get; set; }

        /// <summary>Số sections mà backbone fit được</summary>
        public int FitCount { get; set; }

        /// <summary>Danh sách sections không fit được</summary>
        public List<string> FailedSections { get; set; } = new List<string>();

        /// <summary>Label hiển thị: "2D20 / 2D20"</summary>
        public string DisplayLabel => CountTop == CountBot
            ? $"{CountTop}D{Diameter}"
            : $"T:{CountTop}D{Diameter}/B:{CountBot}D{Diameter}";

        public override string ToString() => $"{DisplayLabel} (Score={TotalScore:F1}, Valid={IsGloballyValid})";
    }

    /// <summary>
    /// Kết quả tổng hợp cho một nhịp (mapping về SpanData).
    /// </summary>
    public class SpanRebarResult
    {
        public int SpanIndex { get; set; }
        public string SpanId { get; set; }

        // Backbone (chạy suốt)
        public Data.RebarInfo TopBackbone { get; set; }
        public Data.RebarInfo BotBackbone { get; set; }

        // Addon per zone (flexible)
        public Dictionary<string, Data.RebarInfo> TopAddons { get; set; } = new Dictionary<string, Data.RebarInfo>();
        public Dictionary<string, Data.RebarInfo> BotAddons { get; set; } = new Dictionary<string, Data.RebarInfo>();

        // Stirrup per zone
        public Dictionary<string, string> Stirrups { get; set; } = new Dictionary<string, string>();

        // Web bar per zone
        public Dictionary<string, string> WebBars { get; set; } = new Dictionary<string, string>();
    }
}
