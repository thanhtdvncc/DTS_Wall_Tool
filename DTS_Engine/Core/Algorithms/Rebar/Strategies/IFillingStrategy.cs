using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Data;

namespace DTS_Engine.Core.Algorithms.Rebar.Strategies
{
    /// <summary>
    /// Interface cho chiến thuật rót thép vào các lớp.
    /// </summary>
    public interface IFillingStrategy
    {
        /// <summary>
        /// Tên chiến thuật (VD: "Greedy", "Balanced").
        /// </summary>
        string StrategyName { get; }

        /// <summary>
        /// Tính toán phân bố thép cho một location.
        /// </summary>
        FillingResult Calculate(FillingContext context);
    }

    /// <summary>
    /// Context cho việc tính toán phân bố thép.
    /// </summary>
    public class FillingContext
    {
        /// <summary>Diện tích thép yêu cầu (cm²)</summary>
        public double RequiredArea { get; set; }

        /// <summary>Diện tích backbone đã cung cấp (cm²)</summary>
        public double BackboneArea { get; set; }

        /// <summary>Số thanh backbone</summary>
        public int BackboneCount { get; set; }

        /// <summary>Đường kính backbone (mm)</summary>
        public int BackboneDiameter { get; set; }

        /// <summary>Capacity tối đa của 1 layer</summary>
        public int LayerCapacity { get; set; }

        /// <summary>Số nhánh đai</summary>
        public int StirrupLegCount { get; set; }

        /// <summary>
        /// Số lớp thép tối đa cho phép (dynamic N-layer).
        /// Mặc định 2 cho backwards compatibility.
        /// </summary>
        public int MaxLayers { get; set; } = 2;

        /// <summary>Settings từ DtsSettings.Beam</summary>
        public DtsSettings Settings { get; set; }

        /// <summary>External constraints nếu có</summary>
        public Models.ExternalConstraints Constraints { get; set; }
    }

    /// <summary>
    /// Kết quả từ IFillingStrategy.Calculate().
    /// Hỗ trợ dynamic N lớp thay vì hardcode 2 lớp.
    /// </summary>
    public class FillingResult
    {
        /// <summary>Có thể bố trí được không?</summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// Số thanh mỗi lớp (index 0 = Layer 1, index 1 = Layer 2, ...).
        /// DYNAMIC: Hỗ trợ N lớp thay vì hardcode 2.
        /// </summary>
        public List<int> LayerCounts { get; set; } = new List<int>();

        /// <summary>Tổng số thanh (computed from LayerCounts)</summary>
        public int TotalBars
        {
            get { return LayerCounts?.Sum() ?? 0; }
        }

        /// <summary>
        /// Số thanh lãng phí do ràng buộc cấu tạo (VD: bump từ 1 lên 2).
        /// Dùng để trừ điểm phương án.
        /// </summary>
        public int WasteCount { get; set; }

        /// <summary>Lý do thất bại nếu IsValid = false</summary>
        public string FailReason { get; set; }

        // ═══════════════════════════════════════════════════════════════
        // HELPER PROPERTIES (Backwards compatibility & convenience)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>Số lớp sử dụng</summary>
        public int LayerCount
        {
            get { return LayerCounts?.Count ?? 0; }
        }
    }
}

