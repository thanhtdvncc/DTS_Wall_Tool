using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Algorithms.Rebar.Rules;

namespace DTS_Engine.Core.Algorithms.Rebar.Models
{
    /// <summary>
    /// Chứa toàn bộ trạng thái thiết kế cho 1 kịch bản.
    /// Được tạo bởi ScenarioGenerator, xử lý bởi các stages sau.
    /// </summary>
    public class SolutionContext
    {
        // ═══════════════════════════════════════════════════════════════
        // INPUT DATA (Set by Pipeline.Execute)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>Nhóm dầm đang thiết kế</summary>
        public BeamGroup Group { get; set; }

        /// <summary>Kết quả phân tích từ SAP (As_req, Av, Vt)</summary>
        public List<BeamResultData> SpanResults { get; set; }

        /// <summary>Settings người dùng</summary>
        public DtsSettings Settings { get; set; }

        /// <summary>Ràng buộc toàn dự án (multi-beam sync)</summary>
        public ProjectConstraints GlobalConstraints { get; set; }

        /// <summary>Ràng buộc cụ thể cho dầm này (user lock)</summary>
        public ExternalConstraints ExternalConstraints { get; set; }

        // ═══════════════════════════════════════════════════════════════
        // SANITIZED GEOMETRY (Set by ScenarioGenerator)
        // ═══════════════════════════════════════════════════════════════

        public double BeamWidth { get; set; }
        public double BeamHeight { get; set; }
        public double TotalLength { get; set; }
        public List<int> AllowedDiameters { get; set; }

        // ═══════════════════════════════════════════════════════════════
        // SCENARIO PARAMETERS (Set by ScenarioGenerator)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>ID kịch bản. VD: "4D20" hoặc "T:4D20/B:3D18"</summary>
        public string ScenarioId { get; set; }

        public int TopBackboneDiameter { get; set; }
        public int BotBackboneDiameter { get; set; }
        public int TopBackboneCount { get; set; }
        public int BotBackboneCount { get; set; }

        /// <summary>Bonus từ việc match PreferredDiameter</summary>
        public double PreferredDiameterBonus { get; set; } = 0;

        /// <summary>
        /// Tổng số thanh thép lãng phí do ràng buộc cấu tạo (VD: 3+1→3+2).
        /// Dùng để phạt điểm phương án.
        /// </summary>
        public int AccumulatedWasteCount { get; set; } = 0;

        // ═══════════════════════════════════════════════════════════════
        // COMPUTED DATA (Set by subsequent stages)
        // ═══════════════════════════════════════════════════════════════

        public RebarProfile LongitudinalProfile { get; set; }
        public StirrupProfile StirrupProfile { get; set; }
        public int StirrupLegCount { get; set; }

        // ═══════════════════════════════════════════════════════════════
        // OUTPUT
        // ═══════════════════════════════════════════════════════════════

        /// <summary>Solution đang build</summary>
        public ContinuousBeamSolution CurrentSolution { get; set; }

        /// <summary>Các xung đột phát hiện (đai vs dọc, etc.)</summary>
        public List<ConflictReport> Conflicts { get; set; } = new List<ConflictReport>();

        /// <summary>Kết quả validate từ các rules</summary>
        public List<ValidationResult> ValidationResults { get; set; } = new List<ValidationResult>();

        // ═══════════════════════════════════════════════════════════════
        // PIPELINE CONTROL
        // ═══════════════════════════════════════════════════════════════

        /// <summary>Kịch bản này còn valid không?</summary>
        public bool IsValid { get; set; } = true;

        /// <summary>Stage nào gây fail</summary>
        public string FailStage { get; set; }

        /// <summary>Tổng điểm trừ từ Warning rules</summary>
        public double TotalPenalty { get; set; } = 0;

        /// <summary>Có lỗi Critical không?</summary>
        public bool HasCriticalError => ValidationResults
            .Any(v => v.Level == SeverityLevel.Critical);

        // ═══════════════════════════════════════════════════════════════
        // METHODS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Clone context để tạo scenario mới.
        /// IMPORTANT: Only clone INPUT and reset OUTPUT/CONTROL.
        /// Scenario params are set by ScenarioGenerator after clone.
        /// </summary>
        public SolutionContext Clone()
        {
            return new SolutionContext
            {
                // INPUT - shared references (immutable during pipeline)
                Group = this.Group,
                SpanResults = this.SpanResults,
                Settings = this.Settings,
                GlobalConstraints = this.GlobalConstraints,
                ExternalConstraints = this.ExternalConstraints,

                // SANITIZED GEOMETRY - copy from seed (set once by ScenarioGenerator)
                BeamWidth = this.BeamWidth,
                BeamHeight = this.BeamHeight,
                TotalLength = this.TotalLength,
                AllowedDiameters = this.AllowedDiameters,

                // SCENARIO - reset (will be set by ScenarioGenerator)
                ScenarioId = null,
                TopBackboneDiameter = 0,
                BotBackboneDiameter = 0,
                TopBackboneCount = 0,
                BotBackboneCount = 0,
                PreferredDiameterBonus = 0,
                AccumulatedWasteCount = 0,

                // OUTPUT - reset
                CurrentSolution = null,
                LongitudinalProfile = null,
                StirrupProfile = null,
                StirrupLegCount = 0,
                ValidationResults = new List<ValidationResult>(),
                Conflicts = new List<ConflictReport>(),

                // CONTROL - reset
                IsValid = true,
                FailStage = null,
                TotalPenalty = 0
            };
        }
    }

    /// <summary>
    /// Báo cáo xung đột (VD: đai không ôm hết thép dọc).
    /// </summary>
    public class ConflictReport
    {
        public string ConflictType { get; set; }
        public string SpanId { get; set; }
        public string Description { get; set; }
        public string SuggestedFix { get; set; }
    }
}
